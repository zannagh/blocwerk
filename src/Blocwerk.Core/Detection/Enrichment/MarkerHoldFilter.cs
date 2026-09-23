// <copyright file="MarkerHoldFilter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>
/// Finds the auto-detected "holds" that are really the printed ArUco marker sheets: a marker is a flat
/// printed square, never a hold, yet the detector happily reports it as one. Pure.
/// </summary>
public static class MarkerHoldFilter
{
    /// <summary>How much each marker quad is grown about its centre before the test (1.15 = +15 %).</summary>
    public const double QuadExpansion = 1.15;

    /// <summary>
    /// The auto-detected holds whose centre lies inside a detected marker's quad, grown by
    /// <see cref="QuadExpansion"/> (the white paper margin around the black square). Manual and virtual
    /// holds are never returned: a person put them there deliberately.
    /// </summary>
    /// <param name="holds">The freshly detected holds (normalized image coordinates).</param>
    /// <param name="markers">The markers detected on the same photo.</param>
    /// <returns>The holds to drop, in input order.</returns>
    public static IReadOnlyList<Hold> HoldsOnMarkers(IReadOnlyList<Hold> holds, IReadOnlyList<DetectedMarker> markers)
    {
        if (markers.Count == 0)
        {
            return [];
        }

        var quads = markers
            .Where(m => m.CornersNormalized.Count >= 3)
            .Select(m => Expand(m.CornersNormalized))
            .ToList();
        return holds
            .Where(h => h.IsAutoDetected && !h.IsVirtual && quads.Any(q => WallProjection.IsPointInPolygon(h.X, h.Y, q)))
            .ToList();
    }

    private static List<(double X, double Y)> Expand(IReadOnlyList<MarkerPoint> corners)
    {
        var cx = corners.Average(c => c.X);
        var cy = corners.Average(c => c.Y);
        return corners
            .Select(c => (cx + ((c.X - cx) * QuadExpansion), cy + ((c.Y - cy) * QuadExpansion)))
            .ToList();
    }
}
