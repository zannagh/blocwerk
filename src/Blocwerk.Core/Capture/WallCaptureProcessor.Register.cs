// <copyright file="WallCaptureProcessor.Register.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>What the registration stage decided about a solved model.</summary>
/// <param name="Json">The document to import: in the active model's frame when registered, else as solved.</param>
/// <param name="Activate">False when it could not be tied to the active model: stored, but not activated.</param>
/// <param name="Refusal">Why it was not activated (admin-safe), when refused.</param>
internal sealed record FrameOutcome(string Json, bool Activate, string? Refusal);

/// <summary>
/// Stage 2b: keep the wall's frame. A new model is registered to the ACTIVE one before it is imported, using
/// only the markers unchanged between the active model's plan revision and this capture's — so hold
/// positions (facet + plane mm) stay valid however the markers were changed. Without enough unchanged
/// markers the model is stored but not activated, with a reason the admin can act on.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task<FrameOutcome> RegisterToActiveAsync(CaptureRun run, string solvedJson, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = run.Capture;
        var active = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == capture.WallId && m.IsActive)
            .Select(m => new { m.Id, m.Json, m.PlanRevision })
            .FirstOrDefaultAsync(ct);
        if (active is null || TryParse(active.Json) is not { } reference || MarkerWorldCorners.Of(reference).Count == 0)
        {
            // The wall's first model (or one without facet frames to tie to): it defines the frame.
            return new FrameOutcome(solvedJson, true, null);
        }

        var eligible = await UnchangedIdsAsync(db, run, active.PlanRevision, ct);
        var result = WallFrameRegistration.Register(reference, WallGeometryDocument.Parse(solvedJson), eligible);
        if (!result.Accepted)
        {
            logger.LogWarning(
                "Capture {CaptureId}: model not tied to active model {ModelId}: {Reason}", capture.Id, active.Id, result.Message);
            return new FrameOutcome(solvedJson, false, result.Message);
        }

        var stamp = new RegistrationStamp(active.Id, active.PlanRevision, capture.PlanRevision);
        var json = WallFrameRegistrationWriter.Rewrite(solvedJson, active.Json, result, eligible, stamp);
        logger.LogInformation(
            "Capture {CaptureId}: registered to model {ModelId} on {Used} markers (rms {Rms:F1} mm, max {Max:F1} mm; "
            + "changed [{Changed}], outliers [{Outliers}]), moved {Rotation:F2}° / {Shift:F0} mm",
            capture.Id, active.Id, result.UsedIds.Count, result.RmsMm, result.MaxMm, string.Join(",", result.ChangedIds),
            string.Join(",", result.OutlierIds), result.Transform!.RotationDeg, result.Transform.TranslationMm);
        return new FrameOutcome(json, true, null);
    }

    /// <summary>
    /// The ids unchanged between the active model's revision and this capture's plan; null (all shared ids)
    /// when both are the same revision; empty when either layout is unknown.
    /// </summary>
    private static async Task<IReadOnlySet<int>?> UnchangedIdsAsync(
        BlocwerkDbContext db, CaptureRun run, int? modelRevision, CancellationToken ct)
    {
        var capture = run.Capture;
        var sameRevision = capture.PlanJson is null
            ? modelRevision is null
            : capture.PlanRevision is { } revision && revision == modelRevision;
        if (sameRevision)
        {
            return null;
        }

        var baselines = new MarkerBaselines(db, capture.WallId);
        var before = await baselines.MarkersAsync(modelRevision, ct);

        // The capture's own snapshot is the truth for its side (it is also what the solver was told).
        var after = run.Layout.Plan?.Markers ?? await baselines.MarkersAsync(null, ct);
        return before is null || after is null ? new HashSet<int>() : MarkerPlanDiff.UnchangedIds(before, after);
    }

    private static WallGeometryDocument? TryParse(string json)
    {
        try
        {
            return WallGeometryDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
