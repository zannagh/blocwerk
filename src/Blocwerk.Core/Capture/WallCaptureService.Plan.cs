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
/// else the wall's saved plan, else the legacy <c>segment*6+role</c> convention. An uploaded plan is
/// saved as the wall's plan only when the wall has none — the uploader is a wall admin (every capture
/// action is) — and otherwise applies to this capture alone: replacing a wall's plan stays a deliberate
/// act in the planner.
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
            capture.PlanJson = plan is null ? null : MarkerPlanJson.ToJson(plan);
            var wallPlanJson = await WallMarkerLayoutResolver.CurrentPlanJsonAsync(db, capture.WallId);
            List<string> notes = plan is null ? [] : PlanNotes(wallPlanJson, capture.PlanJson!);
            if (await ResetDetectionIfNeededAsync(db, capture.Id, before, await DraftLayoutAsync(db, capture)))
            {
                notes.Add(RedetectNote);
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "Capture {CaptureId} marker plan {State} by {UserId}", captureId, plan is null ? "removed" : "uploaded", userId);
            if (plan is not null && wallPlanJson is null && await SaveAsWallPlanAsync(capture.WallId, plan))
            {
                notes.Insert(0, "Saved as this wall's marker plan (it had none).");
            }

            return new CapturePlanResult(true, [], notes);
        }
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
    private static CapturePlanInfo? PlanInfo(WallCapture capture, WallMarkerLayout layout) => layout.IsFromPlan
        ? new CapturePlanInfo(
            capture.PlanJson is not null,
            layout.Segments.Count,
            layout.Markers.Count,
            layout.MaxMarkerId,
            layout.Segments.ToDictionary(s => s.Index, s => (IReadOnlyList<int>)layout.IdsOn(s.Index)))
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

    private static List<string> PlanNotes(string? wallPlanJson, string uploadedJson)
    {
        if (wallPlanJson is null)
        {
            return [];
        }

        var saved = MarkerPlanJson.FromJson(wallPlanJson, out _);
        return saved is not null && MarkerPlanJson.ToJson(saved) == uploadedJson
            ? ["The uploaded plan is the wall's saved marker plan."]
            : ["This capture uses the uploaded plan. The wall's saved marker plan is unchanged — replace it in the marker planner if the uploaded one is right."];
    }

    private async Task<bool> SaveAsWallPlanAsync(Guid wallId, MarkerPlan plan)
    {
        if (markerPlans is null)
        {
            return false;
        }

        var result = await markerPlans.SavePlanAsync(wallId, plan);
        return result.Saved;
    }
}
