// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// A plan triangle with its right angle at the bottom stands on the floor, but its solved extent ends a margin below its
/// lowest markers. When a neighbouring facet reaches lower (the kickboard, the floor), the triangle's bottom edge is
/// lowered to that facet's lowest edge, its legs and hypotenuse kept on their lines; then it is cut where it passes
/// behind that facet's plane, so it meets it instead of poking through (<see cref="Wall3DFacetOutlines"/>).
/// </summary>
public static class FacetFloorReach
{
    /// <summary>The bottom edge is lowered by at most this (mm, along up); more is not the same floor.</summary>
    public const double MaxDropMm = 250;

    /// <summary>A facet counts as a neighbour when its extent comes within this (mm) of the triangle's bottom edge.</summary>
    public const double MaxNeighbourGapMm = 150;

    /// <summary>The facets, each bottom-cornered triangle's outline reaching down to its lowest neighbour.</summary>
    public static List<Wall3DFacet> Apply(
        List<Wall3DFacet> facets, WallGeometryDocument doc, IReadOnlyDictionary<int, PlanTriangle> triangles)
    {
        double[] up = doc.World?.Up is { Length: 3 } u ? u : [0, 0, 1];
        return facets.Select(f => triangles.TryGetValue(f.Segment, out var t) && IsBottom(t.RightAngle)
                ? Reach(f, facets, doc, up) ?? f
                : f)
            .ToList();
    }

    private static bool IsBottom(TriangleCorner corner) =>
        corner is TriangleCorner.BottomLeft or TriangleCorner.BottomRight;

    private static Wall3DFacet? Reach(Wall3DFacet facet, List<Wall3DFacet> facets, WallGeometryDocument doc, double[] up)
    {
        var rise = Wall3DFacetOutlines.Dot(facet.V, up);
        if (facet.Outline is not { Count: >= 3 } outline || rise < TriangleKeptSide.MinUpComponent)
        {
            return null;
        }

        var bottomB = outline.Min(p => p[1]);
        var bottom = outline.Where(p => p[1] - bottomB < Wall3DFacetOutlines.MinEdgeMm).ToList();
        if (bottom.Count < 2)
        {
            return null;
        }

        var height = Height(Wall3DFacetOutlines.World(facet, bottom[0][0], bottom[0][1]), up);
        var floor = facets
            .Where(g => g.Id != facet.Id && Gap(facet, bottom, g) <= MaxNeighbourGapMm)
            .Select(g => (Facet: g, Low: g.Corners.Min(c => Height(c, up))))
            .Where(g => height - g.Low > 1 && height - g.Low <= MaxDropMm)
            .OrderBy(g => g.Low)
            .FirstOrDefault();
        if (floor.Facet is null)
        {
            return null;
        }

        var lowered = Lower(outline, bottomB, bottomB - ((height - floor.Low) / rise));
        var centroid = Wall3DFacetOutlines.MarkerCentroid(doc, facet.Id);
        return Wall3DFacetOutlines.WithShape(facet, MeetFloor(facet, floor.Facet, lowered, centroid) ?? lowered);
    }

    /// <summary>The outline with its bottom corners moved down to <paramref name="newB"/> along their upper edges.</summary>
    private static List<double[]> Lower(IReadOnlyList<double[]> outline, double bottomB, double newB)
    {
        bool AtBottom(double[] p) => p[1] - bottomB < Wall3DFacetOutlines.MinEdgeMm;
        var result = new List<double[]>(outline.Count);
        for (var i = 0; i < outline.Count; i++)
        {
            var p = outline[i];
            var prev = outline[(i + outline.Count - 1) % outline.Count];
            var next = outline[(i + 1) % outline.Count];
            var upper = !AtBottom(prev) ? prev : !AtBottom(next) ? next : null;
            if (!AtBottom(p) || upper is null)
            {
                result.Add(AtBottom(p) ? [p[0], newB] : p);
                continue;
            }

            var t = (newB - upper[1]) / (p[1] - upper[1]);
            result.Add([upper[0] + (t * (p[0] - upper[0])), newB]);
        }

        return result;
    }

    /// <summary>The lowered outline cut at the floor facet's plane, on its markers' side, when that seam crosses its bottom edge.</summary>
    private static IReadOnlyList<double[]>? MeetFloor(
        Wall3DFacet facet, Wall3DFacet floor, List<double[]> lowered, (double A, double B)? centroid)
    {
        if (centroid is not { } c || Wall3DFacetOutlines.SeamSide(facet, floor) is not { } side)
        {
            return null;
        }

        var sign = Math.Sign(side(c.A, c.B));
        var newB = lowered.Min(p => p[1]);
        var ends = lowered.Where(p => p[1] - newB < Wall3DFacetOutlines.MinEdgeMm).Select(p => sign * side(p[0], p[1])).ToList();
        return sign != 0 && ends.Min() < 0 && ends.Max() > 0
            ? Wall3DFacetOutlines.ClipHalf(lowered, (a, b) => sign * side(a, b))
            : null;
    }

    /// <summary>Least distance (mm) from the bottom edge to <paramref name="other"/>'s extent.</summary>
    private static double Gap(Wall3DFacet facet, List<double[]> bottom, Wall3DFacet other)
    {
        const int Steps = 10;
        var s = bottom.MinBy(p => p[0])!;
        var t = bottom.MaxBy(p => p[0])!;
        var best = double.PositiveInfinity;
        for (var i = 0; i <= Steps; i++)
        {
            var w = Wall3DFacetOutlines.World(facet, s[0] + ((t[0] - s[0]) * i / Steps), s[1] + ((t[1] - s[1]) * i / Steps));
            best = Math.Min(best, FacetSeamTrim.DistanceToExtent(other, w));
        }

        return best;
    }

    private static double Height(double[] p, double[] up) => Wall3DFacetOutlines.Dot(p, up);
}
