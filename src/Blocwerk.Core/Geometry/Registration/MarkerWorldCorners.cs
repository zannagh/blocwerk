// <copyright file="MarkerWorldCorners.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>
/// Marker corners in the model's world frame, rebuilt from each marker's plane corners and its facet's
/// frame (<c>origin + a·u + b·v</c>) — the same numbers hold positions use, and available even in documents
/// that do not carry <c>cornersWorldMm</c>.
/// </summary>
internal static class MarkerWorldCorners
{
    /// <summary>Marker id → its four world corners, for every marker whose facet has a full frame.</summary>
    public static IReadOnlyDictionary<int, double[][]> Of(WallGeometryDocument document)
    {
        var facets = document.Segments.SelectMany(s => s.Facets).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var corners = new Dictionary<int, double[][]>();
        foreach (var marker in document.Markers)
        {
            if (!facets.TryGetValue(marker.Facet, out var facet) || !HasFrame(facet) || marker.CornersPlaneMm.Count != 4
                || marker.CornersPlaneMm.Any(c => c.Length < 2))
            {
                continue;
            }

            corners.TryAdd(marker.Id, marker.CornersPlaneMm.Select(c => ToWorld(facet, c[0], c[1])).ToArray());
        }

        return corners;
    }

    /// <summary>True when the facet carries origin, u and v.</summary>
    public static bool HasFrame(WallGeometryFacet facet) =>
        facet.Origin is { Length: 3 } && facet.U is { Length: 3 } && facet.V is { Length: 3 };

    /// <summary>A plane point of <paramref name="facet"/> in world mm.</summary>
    public static double[] ToWorld(WallGeometryFacet facet, double a, double b) =>
        Vec3.Add(facet.Origin!, Vec3.Add(Vec3.Scale(facet.U!, a), Vec3.Scale(facet.V!, b)));
}
