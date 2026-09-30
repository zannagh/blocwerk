// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Where two facets meet, each solved extent runs a margin past the other's plane, so one pokes through the other at the
/// seam. A facet's outline is trimmed at a neighbour's plane when the part beyond it is small (at most
/// <see cref="MaxOvershootMm"/>), lies on the far side from the facet's markers, and the neighbour runs along most of
/// the cut (<see cref="MinSeamCoverage"/>; a plane is infinite, so a neighbour covering only one end of the edge, like a
/// nearly coplanar piece next to the one it meets, would cut the rest of the edge at the wrong height). Larger crossings
/// are real geometry (a facet standing in front of another's middle) and are left alone. Viewer only
/// (<see cref="Wall3DFacetOutlines"/>).
/// </summary>
public static class FacetSeamTrim
{
    /// <summary>Only an overshoot up to this (mm, in the facet's plane) is trimmed.</summary>
    public const double MaxOvershootMm = 100;

    /// <summary>Overshoots up to this (mm) are ignored: the facets already meet.</summary>
    public const double MinOvershootMm = 2;

    /// <summary>The share of the cut edge that must lie within <see cref="MaxOvershootMm"/> of the neighbour's extent.</summary>
    public const double MinSeamCoverage = 0.5;

    /// <summary>The facets, each outline trimmed at the neighbours' planes it pokes through at their seams.</summary>
    public static List<Wall3DFacet> Apply(List<Wall3DFacet> facets, WallGeometryDocument doc) =>
        facets.Select(f => Trim(f, facets, doc)).ToList();

    /// <summary>Distance (mm) from world point <paramref name="w"/> to <paramref name="facet"/>'s extent rectangle.</summary>
    public static double DistanceToExtent(Wall3DFacet facet, double[] w)
    {
        double[] d = [w[0] - facet.Origin[0], w[1] - facet.Origin[1], w[2] - facet.Origin[2]];
        var inPlane = Wall3DFacetOutlines.RectDistance(facet.Extent, Wall3DFacetOutlines.Dot(d, facet.U), Wall3DFacetOutlines.Dot(d, facet.V));
        var off = Wall3DFacetOutlines.Dot(d, facet.Normal);
        return Math.Sqrt((inPlane * inPlane) + (off * off));
    }

    private static Wall3DFacet Trim(Wall3DFacet facet, List<Wall3DFacet> facets, WallGeometryDocument doc)
    {
        if (Wall3DFacetOutlines.MarkerCentroid(doc, facet.Id) is not { } centroid)
        {
            return facet;
        }

        var outline = facet.Outline ?? Wall3DFacetOutlines.RectCorners(facet.Extent);
        var trimmed = false;
        foreach (var other in facets.Where(g => g.Id != facet.Id))
        {
            if (TrimAt(facet, other, outline, centroid) is { } cut)
            {
                outline = cut;
                trimmed = true;
            }
        }

        return trimmed ? Wall3DFacetOutlines.WithShape(facet, outline) : facet;
    }

    private static IReadOnlyList<double[]>? TrimAt(
        Wall3DFacet facet, Wall3DFacet other, IReadOnlyList<double[]> outline, (double A, double B) centroid)
    {
        if (Wall3DFacetOutlines.SeamSide(facet, other) is not { } side)
        {
            return null;
        }

        var markers = side(centroid.A, centroid.B);
        if (Math.Abs(markers) <= MaxOvershootMm)
        {
            return null;
        }

        var sign = Math.Sign(markers);
        var overshoot = outline.Max(p => -sign * side(p[0], p[1]));
        if (overshoot <= MinOvershootMm || overshoot > MaxOvershootMm)
        {
            return null;
        }

        var cut = Wall3DFacetOutlines.ClipHalf(outline, (a, b) => sign * side(a, b));
        return cut is not null && SeamCoverage(facet, other, cut, side) >= MinSeamCoverage ? cut : null;
    }

    /// <summary>The share of the cut edge (the outline's points on the seam) within <see cref="MaxOvershootMm"/> of <paramref name="other"/>'s extent.</summary>
    private static double SeamCoverage(
        Wall3DFacet facet, Wall3DFacet other, IReadOnlyList<double[]> cut, Func<double, double, double> side)
    {
        var onSeam = cut.Where(p => Math.Abs(side(p[0], p[1])) < 1).ToList();
        if (onSeam.Count < 2)
        {
            return 0;
        }

        const int Steps = 20;
        var (s, t) = onSeam.SelectMany(p => onSeam.Select(q => (p, q)))
            .MaxBy(pq => Math.Abs(pq.p[0] - pq.q[0]) + Math.Abs(pq.p[1] - pq.q[1]));
        var near = 0;
        for (var i = 0; i <= Steps; i++)
        {
            var w = Wall3DFacetOutlines.World(facet, s[0] + ((t[0] - s[0]) * i / Steps), s[1] + ((t[1] - s[1]) * i / Steps));
            near += DistanceToExtent(other, w) <= MaxOvershootMm ? 1 : 0;
        }

        return near / (Steps + 1.0);
    }
}
