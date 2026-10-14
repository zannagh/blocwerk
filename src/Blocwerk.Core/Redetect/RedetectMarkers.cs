// <copyright file="RedetectMarkers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// The printed markers on a panel photo, as quads in photo fractions: the ones stored for the photo, and the ones a
/// lenient pass of the marker detector finds on it (a marker seen at a steep angle, cut by the photo's edge, or one
/// whose id repeats). A detection on a marker is a marker, never a hold.
/// </summary>
internal static class RedetectMarkers
{
    /// <summary>The quads of the panel's live photo.</summary>
    /// <param name="db">The context.</param>
    /// <param name="panel">The panel.</param>
    /// <param name="photo">Its photo.</param>
    /// <param name="markers">The marker detector, when the host has one.</param>
    /// <param name="logger">Hears about a failed marker pass.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The quads, corners in photo fractions.</returns>
    public static async Task<List<IReadOnlyList<(double X, double Y)>>> LoadAsync(
        BlocwerkDbContext db, WallPanel panel, byte[] photo, IMarkerDetectionService? markers, ILogger logger, CancellationToken ct)
    {
        var quads = new List<IReadOnlyList<(double X, double Y)>>();
        var rows = await db.WallMarkerObservations.AsNoTracking()
            .Where(o => o.WallPanelId == panel.Id && !o.FromStagedPhoto && !o.Synthetic)
            .Select(o => o.CornersJson)
            .ToListAsync(ct);
        foreach (var json in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<double[][]>(json) is { Length: 4 } corners && corners.All(c => c.Length >= 2))
                {
                    quads.Add([.. corners.Select(c => (c[0], c[1]))]);
                }
            }
            catch (JsonException)
            {
                // An unreadable row is simply not a marker to avoid.
            }
        }

        if (markers is null)
        {
            return quads;
        }

        try
        {
            var found = await markers.DetectAsync(photo, WallBigUpdateService.RescueMarkerOptions, ct);
            double w = Math.Max(1, found.ImageWidth), h = Math.Max(1, found.ImageHeight);
            quads.AddRange(found.Markers.Select(m => m.CornersPx)
                .Concat(found.Rejected.Where(r => r.Reason == MarkerRejectionReason.DuplicateId).Select(r => r.CornersPx))
                .Select(q => (IReadOnlyList<(double X, double Y)>)[.. q.Select(c => (c.X / w, c.Y / h))]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Re-detect's marker pass failed on panel {PanelId}; only the stored markers are avoided", panel.Id);
        }

        return quads;
    }

    /// <summary>
    /// True when the detection is a marker: its centre lies on one (the white print border included), or its outline
    /// takes in a marker's middle.
    /// </summary>
    /// <param name="quads">The markers.</param>
    /// <param name="detection">The detection.</param>
    /// <returns>Whether it is on a marker.</returns>
    public static bool On(IReadOnlyList<IReadOnlyList<(double X, double Y)>> quads, Hold detection)
    {
        var outline = detection.ShapePoints is { Count: >= 3 } ? HoldShapePolygon.Of(detection) : null;
        return quads.Any(q => NewHoldTriage.InsideGrown(q, detection.X, detection.Y)
            || (outline is not null && ShapeGeometry.Contains(outline, new P2(q.Average(c => c.X), q.Average(c => c.Y)))));
    }
}
