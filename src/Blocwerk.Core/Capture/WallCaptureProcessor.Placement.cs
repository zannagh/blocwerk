// <copyright file="WallCaptureProcessor.Placement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>After the solve: "did I place the markers right?" — the plan against what was measured.</summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>
    /// Stores the planned-vs-observed check on the capture (plan walls only). Advisory: a failure here
    /// is logged and never fails the capture — the model is already active.
    /// </summary>
    /// <param name="run">The capture.</param>
    /// <param name="geometryJson">The solved document, or null to read the imported model back.</param>
    /// <param name="ct">Cancellation.</param>
    private async Task CheckPlacementAsync(CaptureRun run, string? geometryJson, CancellationToken ct)
    {
        if (!run.Layout.IsFromPlan)
        {
            return;
        }

        try
        {
            var json = geometryJson ?? await ModelJsonAsync(run.Capture.GeometryModelId, ct);
            if (json is null)
            {
                return;
            }

            var photos = await LoadPhotosAsync(run.Capture.Id, ct);
            var detected = photos
                .SelectMany(p => CaptureComputeDocuments.UsableMarkers(run.Layout, p.MarkersJson))
                .Select(m => m.Id)
                .ToHashSet();
            var check = MarkerPlacementChecker.Check(run.Layout, WallGeometryDocument.Parse(json), detected);
            var stored = JsonSerializer.Serialize(check);
            await UpdateAsync(run.Capture.Id, c => c.PlacementCheckJson = stored, ct);
            logger.LogInformation(
                "Capture {CaptureId} placement check: {Solved}/{Planned} planned markers solved, {Findings} finding(s)",
                run.Capture.Id, check.SolvedMarkers, check.PlannedMarkers, check.Findings.Count);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the placement check could not read the model", run.Capture.Id);
        }
    }

    private async Task<string?> ModelJsonAsync(Guid? modelId, CancellationToken ct)
    {
        if (modelId is null)
        {
            return null;
        }

        await using var db = dbContextFactory.CreateDbContext();
        return await db.WallGeometryModels.Where(m => m.Id == modelId).Select(m => m.Json).FirstOrDefaultAsync(ct);
    }
}
