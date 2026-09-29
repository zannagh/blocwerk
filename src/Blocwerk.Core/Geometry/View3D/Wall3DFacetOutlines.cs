// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Cuts a triangular segment's facet to its shape. The marker plan only says WHICH segments are right
/// triangles and which parent their hypotenuse lies against (its millimetres need not match the solve);
/// the hypotenuse itself is where the facet's solved plane meets the parent facet's. The facet's extent
/// rectangle is clipped by that line, keeping the side its markers are on.
/// </summary>
public static class Wall3DFacetOutlines
{
    /// <summary>Planes closer to parallel than this (sine of their angle, ~10°) give no reliable seam.</summary>
    public const double MinPlaneAngleSin = 0.17;

    /// <summary>A marker centroid this close to the seam does not say which side the facet is on.</summary>
    public const double MinCentroidDistanceMm = 10;

    /// <summary>Triangle segment index → the parent segment its hypotenuse is attached to.</summary>
    public static IReadOnlyDictionary<int, int> HypotenuseParents(MarkerPlan? plan) =>
        plan?.Segments
            .Where(s => s.Shape == SegmentShape.Triangle && s.AttachedTo is { OwnEdge: SegmentEdge.Hypotenuse })
            .ToDictionary(s => s.Index, s => s.AttachedTo!.ParentIndex)
        ?? new Dictionary<int, int>();

    /// <summary>The facets, each triangle's with its clipped <see cref="Wall3DFacet.Outline"/> where one can be found.</summary>
    public static List<Wall3DFacet> Apply(List<Wall3DFacet> facets, WallGeometryDocument doc, IReadOnlyDictionary<int, int>? parents)
    {
        if (parents is not { Count: > 0 })
        {
            return facets;
        }

        return facets.Select(f => WithOutline(f, facets, doc, parents)).ToList();
    }

    /// <summary>
    /// The facet's extent clipped where its plane meets <paramref name="parent"/>'s, keeping the side of
    /// <paramref name="inside"/> ([a, b] points, counter-clockwise); null when the clip is unreliable or cuts nothing.
    /// </summary>
    public static IReadOnlyList<double[]>? Clip(Wall3DFacet facet, Wall3DFacet parent, (double A, double B) inside)
    {
        var n = parent.Normal;
        var alpha = Dot(n, facet.U);
        var beta = Dot(n, facet.V);
        var norm = Math.Sqrt((alpha * alpha) + (beta * beta));
        if (norm < MinPlaneAngleSin)
        {
            return null;
        }

        var gamma = Dot(n, parent.Origin) - Dot(n, facet.Origin);
        double Side(double a, double b) => ((alpha * a) + (beta * b) - gamma) / norm;
        var keep = Side(inside.A, inside.B);
        if (Math.Abs(keep) < MinCentroidDistanceMm)
        {
            return null;
        }

        var sign = Math.Sign(keep);
        return ClipRect(facet.Extent, (a, b) => sign * Side(a, b));
    }

    /// <summary>The rectangle's part where <paramref name="side"/> ≥ 0; null when that is all of it or less than a triangle.</summary>
    public static IReadOnlyList<double[]>? ClipRect(PlaneRectMm rect, Func<double, double, double> side)
    {
        double[][] corners = [[rect.AMin, rect.BMin], [rect.AMax, rect.BMin], [rect.AMax, rect.BMax], [rect.AMin, rect.BMax]];
        if (corners.All(c => side(c[0], c[1]) >= 0))
        {
            return null;
        }

        var result = new List<double[]>();
        for (var i = 0; i < corners.Length; i++)
        {
            var p = corners[i];
            var q = corners[(i + 1) % corners.Length];
            var sp = side(p[0], p[1]);
            var sq = side(q[0], q[1]);
            if (sp >= 0)
            {
                AddDistinct(result, p);
            }

            if ((sp >= 0) != (sq >= 0))
            {
                var t = sp / (sp - sq);
                AddDistinct(result, [p[0] + (t * (q[0] - p[0])), p[1] + (t * (q[1] - p[1]))]);
            }
        }

        if (result.Count > 1 && Same(result[0], result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result.Count >= 3 ? result : null;
    }

    private static Wall3DFacet WithOutline(
        Wall3DFacet facet, List<Wall3DFacet> facets, WallGeometryDocument doc, IReadOnlyDictionary<int, int> parents)
    {
        if (!parents.TryGetValue(facet.Segment, out var parentIndex))
        {
            return facet;
        }

        var parent = facets
            .Where(f => f.Segment == parentIndex && f.Id != facet.Id)
            .MinBy(f => Distance(Centre(f), Centre(facet)));
        if (parent is null || MarkerCentroid(doc, facet.Id) is not { } centroid || Clip(facet, parent, centroid) is not { } outline)
        {
            return facet;
        }

        return facet with { Corners = outline.Select(p => World(facet, p[0], p[1])).ToList(), Outline = outline };
    }

    private static (double A, double B)? MarkerCentroid(WallGeometryDocument doc, string facetId)
    {
        var corners = doc.Markers
            .Where(m => m.Facet == facetId)
            .SelectMany(m => m.CornersPlaneMm)
            .Where(c => c.Length >= 2)
            .ToList();
        return corners.Count == 0 ? null : (corners.Average(c => c[0]), corners.Average(c => c[1]));
    }

    private static void AddDistinct(List<double[]> points, double[] p)
    {
        if (points.Count == 0 || !Same(points[^1], p))
        {
            points.Add(p);
        }
    }

    private static bool Same(double[] p, double[] q) => Math.Abs(p[0] - q[0]) < 1e-6 && Math.Abs(p[1] - q[1]) < 1e-6;

    private static double[] Centre(Wall3DFacet f) =>
        World(f, (f.Extent.AMin + f.Extent.AMax) / 2, (f.Extent.BMin + f.Extent.BMax) / 2);

    private static double[] World(Wall3DFacet f, double a, double b) =>
    [
        f.Origin[0] + (a * f.U[0]) + (b * f.V[0]),
        f.Origin[1] + (a * f.U[1]) + (b * f.V[1]),
        f.Origin[2] + (a * f.U[2]) + (b * f.V[2]),
    ];

    private static double Dot(double[] p, double[] q) => (p[0] * q[0]) + (p[1] * q[1]) + (p[2] * q[2]);

    private static double Distance(double[] p, double[] q) =>
        Math.Sqrt(((p[0] - q[0]) * (p[0] - q[0])) + ((p[1] - q[1]) * (p[1] - q[1])) + ((p[2] - q[2]) * (p[2] - q[2])));
}
