// <copyright file="WallCaptureService.Plan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The marker plan that travels with a photo dump. A draft uses the plan JSON uploaded with its photos,
/// else the wall's saved plan, else the legacy <c>segment*6+role</c> convention. An uploaded plan that
/// differs from the wall's current one is saved as the wall's NEXT plan revision (the uploader is a wall
/// admin — every capture action is): markers change over a wall's life, and every capture, model and photo
/// records the revision it was made with, so nothing mixes old and new markers.
/// </summary>
public sealed partial class WallCaptureService
{
    private const string RedetectNote =
        "The photos will be searched for the plan's markers again when the computation starts.";

    public async Task<CapturePlanResult> AttachPlanAsync(Guid captureId, string? planJson)
    {
        MarkerPlan? plan = null;
        if (!string.IsNullOrWhiteSpace(planJson))
        {
            plan = MarkerPlanJson.FromJson(planJson, out var parseErrors);
            IReadOnlyList<string> errors = plan is null
                ? parseErrors
                : MarkerPlanValidator.Validate(plan).Where(i => i.Severity == PlanIssueSeverity.Error).Select(i => i.Message).ToList();
            if (errors.Count > 0)
            {
                return new CapturePlanResult(false, errors, []);
            }
        }

        var (db, userId, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            var before = await DraftLayoutAsync(db, capture);
            var notes = plan is null ? [] : await SaveAsRevisionAsync(db, capture, plan);
            capture.PlanJson = plan is null ? null : MarkerPlanJson.ToJson(plan);
            if (plan is null)
            {
                capture.PlanRevision = null;
            }

            if (await ResetDetectionIfNeededAsync(db, capture.Id, before, await DraftLayoutAsync(db, capture)))
            {
                notes.Add(RedetectNote);
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "Capture {CaptureId} marker plan {State} by {UserId} (revision {Revision})",
                captureId, plan is null ? "removed" : "uploaded", userId, capture.PlanRevision);
            return new CapturePlanResult(true, [], notes);
        }
    }

    public async Task<CapturePlanResult> UsePlanRevisionAsync(Guid captureId, int revision)
    {
        var (db, userId, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            var json = await db.WallMarkerPlans
                .Where(p => p.WallId == capture.WallId && p.Revision == revision)
                .Select(p => p.Json)
                .FirstOrDefaultAsync();
            if (json is null || MarkerPlanJson.FromJson(json, out _) is null)
            {
                return new CapturePlanResult(false, [$"This wall has no marker plan revision {revision}."], []);
            }

            var before = await DraftLayoutAsync(db, capture);
            (capture.PlanJson, capture.PlanRevision) = (json, revision);
            List<string> notes = [$"This capture uses revision {revision} of the wall's marker plan."];
            if (await ResetDetectionIfNeededAsync(db, capture.Id, before, await DraftLayoutAsync(db, capture)))
            {
                notes.Add(RedetectNote);
            }

            await db.SaveChangesAsync();
            logger.LogInformation("Capture {CaptureId} pinned to plan revision {Revision} by {UserId}", captureId, revision, userId);
            return new CapturePlanResult(true, [], notes);
        }
    }

    /// <summary>
    /// Makes the uploaded plan a revision of the wall's plan (a new one when it differs from the current)
    /// and pins the draft to it. Returns the notes for the admin, incl. what changed since the last capture.
    /// </summary>
    private async Task<List<string>> SaveAsRevisionAsync(BlocwerkDbContext db, WallCapture capture, MarkerPlan plan)
    {
        var hadPlan = await WallMarkerLayoutResolver.CurrentPlanJsonAsync(db, capture.WallId) is not null;
        var saved = markerPlans is null ? null : await markerPlans.SavePlanAsync(capture.WallId, plan);
        capture.PlanRevision = saved is { Saved: true } ? saved.Revision : null;
        if (saved is not { Saved: true })
        {
            return ["This capture uses the uploaded plan. It could not be saved as the wall's marker plan."];
        }

        var notes = new List<string>
        {
            saved.Unchanged
                ? $"The uploaded plan is the wall's saved marker plan (revision {saved.Revision})."
                : hadPlan
                    ? $"Saved as revision {saved.Revision} of this wall's marker plan: it is the wall's plan from now on."
                    : $"Saved as this wall's marker plan (it had none) — revision {saved.Revision}.",
        };
        if (await markerPlans!.GetChangesSinceLastCaptureAsync(capture.WallId) is { Diff.IsEmpty: false } changes)
        {
            notes.Add(ChangesNote(changes));
            notes.AddRange(changes.Advice);
        }

        return notes;
    }

    private static string ChangesNote(MarkerPlanChanges changes)
    {
        var d = changes.Diff;
        var parts = new (string Label, IReadOnlyList<int> Ids)[]
        {
            ("added", d.Added), ("removed", d.Removed), ("resized", d.Resized), ("moved", d.Moved), ("moved to another surface", d.Reassigned),
        };
        return $"Since {changes.BaselineLabel}: "
               + string.Join("; ", parts.Where(p => p.Ids.Count > 0).Select(p => $"{p.Label} {string.Join(", ", p.Ids)}"))
               + $". {d.UnchangedIds.Count} marker(s) unchanged.";
    }

    /// <summary>The layout a draft runs with now: its uploaded plan, else the wall's, else legacy.</summary>
    private static async Task<WallMarkerLayout> DraftLayoutAsync(BlocwerkDbContext db, WallCapture capture)
    {
        var size = await db.Walls.IgnoreQueryFilters()
            .Where(w => w.Id == capture.WallId)
            .Select(w => w.MarkerSizeMm)
            .FirstOrDefaultAsync();
        var json = capture.PlanJson ?? await WallMarkerLayoutResolver.CurrentPlanJsonAsync(db, capture.WallId);
        return WallMarkerLayoutResolver.Resolve(json, size);
    }

    /// <summary>What the draft's plan is, for the upload panel; null without one (legacy ids).</summary>
    private static CapturePlanInfo? PlanInfo(WallCapture capture, WallMarkerLayout layout, int? revision) => layout.IsFromPlan
        ? new CapturePlanInfo(
            capture.PlanJson is not null,
            layout.Segments.Count,
            layout.Markers.Count,
            layout.MaxMarkerId,
            layout.Segments.ToDictionary(s => s.Index, s => (IReadOnlyList<int>)layout.IdsOn(s.Index)),
            revision)
        : null;

    /// <summary>
    /// Stored detections are usable only when they were made with (at least) the new layout's ids; a plan
    /// that adds ids clears them, so the pipeline's detection stage finds the plan's markers.
    /// </summary>
    private static async Task<bool> ResetDetectionIfNeededAsync(
        BlocwerkDbContext db, Guid captureId, WallMarkerLayout before, WallMarkerLayout after)
    {
        if (after.AllowedIds.IsSubsetOf(before.AllowedIds))
        {
            return false;
        }

        var photos = await db.WallCapturePhotos.Where(p => p.CaptureId == captureId && p.MarkersJson != null).ToListAsync();
        foreach (var photo in photos)
        {
            photo.MarkersJson = null;
        }

        return photos.Count > 0;
    }
}
