// <copyright file="Wall3DPhotoMarkerLoader.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Loads the stored marker observations of a wall's committed panel photos for the 3D view, so hold
/// outlines map onto their facet through the photo's own marker homography. Only the observation
/// rows are read (never the photo bytes); the photo's pixel size is recovered from the markers'
/// stored side lengths, which is all the homography fit needs (see <see cref="Wall3DPhotoMarkers"/>).
/// </summary>
public static class Wall3DPhotoMarkerLoader
{
    /// <summary>Grid side used when no observation carries a usable pixel side length.</summary>
    public const double DefaultScale = 4000;

    /// <summary>The wall's committed (non-staged) observations, per (panel, generation).</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The markers per photo.</returns>
    public static async Task<Dictionary<Wall3DPhotoKey, Wall3DPhotoMarkers>> LoadAsync(
        BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var rows = await db.WallMarkerObservations
            .AsNoTracking()
            .Where(o => o.WallPanel.WallId == wallId && !o.FromStagedPhoto)
            .ToListAsync(ct);
        return rows
            .GroupBy(o => new Wall3DPhotoKey(o.WallPanelId, o.PanelGeneration))
            .ToDictionary(g => g.Key, g => ToPhoto(g.ToList()));
    }

    /// <summary>One photo's rows as markers in a square grid whose scale matches their stored pixel sides.</summary>
    /// <param name="rows">The photo's observation rows.</param>
    /// <returns>The markers and the grid scale.</returns>
    public static Wall3DPhotoMarkers ToPhoto(IReadOnlyList<WallMarkerObservation> rows)
    {
        var scale = EstimateScale(rows);
        var size = (int)Math.Round(scale);
        return new Wall3DPhotoMarkers(HoldMetricPlanner.MarkersFromObservations(rows, size, size), size);
    }

    /// <summary>
    /// Median of (stored side in px) / (mean normalised edge length) over the rows: the photo's pixel
    /// scale, exact for a square photo and within its aspect ratio otherwise. It only sizes the robust
    /// fit's inlier threshold, so that error is harmless.
    /// </summary>
    private static double EstimateScale(IReadOnlyList<WallMarkerObservation> rows)
    {
        var ratios = new List<double>();
        foreach (var row in rows.Where(r => r.SidePx > 0))
        {
            var corners = ParseCorners(row.CornersJson);
            if (corners is null)
            {
                continue;
            }

            var edge = Enumerable.Range(0, 4)
                .Average(i => Math.Sqrt(Math.Pow(corners[(i + 1) % 4][0] - corners[i][0], 2) + Math.Pow(corners[(i + 1) % 4][1] - corners[i][1], 2)));
            if (edge > 1e-9)
            {
                ratios.Add(row.SidePx / edge);
            }
        }

        if (ratios.Count == 0)
        {
            return DefaultScale;
        }

        ratios.Sort();
        return ratios[ratios.Count / 2];
    }

    private static double[][]? ParseCorners(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<double[][]>(json);
            return raw is { Length: 4 } && raw.All(c => c is { Length: 2 }) ? raw : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
