// <copyright file="WallCaptureProcessor.Placement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;
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
        var json = geometryJson ?? await ModelJsonAsync(run.Capture.GeometryModelId, ct);
        if (json is not null && await PlacementCheckJsonAsync(run, json, ct) is { } stored)
        {
            await UpdateAsync(run.Capture.Id, c => c.PlacementCheckJson = stored, ct);
        }
    }

    /// <summary>The planned-vs-observed check of <paramref name="json"/> as stored; null off a plan or when the model cannot be read.</summary>
    private async Task<string?> PlacementCheckJsonAsync(CaptureRun run, string json, CancellationToken ct)
    {
        if (!run.Layout.IsFromPlan)
        {
            return null;
        }

        try
        {
            var photos = await LoadPhotosAsync(run.Capture.Id, ct);
            var detected = photos
                .SelectMany(p => CaptureComputeDocuments.UsableMarkers(run.Layout, p.MarkersJson))
                .Select(m => m.Id)
                .ToHashSet();
            var labels = photos.ToDictionary(p => CaptureComputeDocuments.PhotoName(p.Index), PhotoLabel);
            var check = MarkerPlacementChecker.Check(
                run.Layout,
                WallGeometryDocument.Parse(json),
                detected,
                name => labels.GetValueOrDefault(name, name));
            logger.LogInformation(
                "Capture {CaptureId} placement check: {Solved}/{Planned} planned markers solved, {Findings} finding(s)",
                run.Capture.Id, check.SolvedMarkers, check.PlannedMarkers, check.Findings.Count);
            return JsonSerializer.Serialize(check);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the placement check could not read the model", run.Capture.Id);
            return null;
        }
    }

    /// <summary>What the admin knows a photo as: its uploaded file name without extension, else its number.</summary>
    private static string PhotoLabel(WallCapturePhoto photo) =>
        string.IsNullOrWhiteSpace(photo.OriginalFileName)
            ? $"photo {photo.Index + 1}"
            : Path.GetFileNameWithoutExtension(photo.OriginalFileName);

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
