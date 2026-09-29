// <copyright file="FacetSeams.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// Where a facet's extent ends but the wall goes on: a near-coplanar neighbouring facet continues it across that edge.
/// Extents are the markers' bounds plus a margin, so such an edge is a seam between facets, not the wall's edge, and a
/// volume reaching into it is not cut off by the wall's end.
/// </summary>
public sealed class FacetSeams
{
    private readonly FacetFrame frame;
    private readonly PlaneRectMm extent;
    private readonly List<(FacetFrame Frame, PlaneRectMm Extent)> neighbours;
    private readonly VolumeDetectionOptions options;

    private FacetSeams(FacetFrame own, PlaneRectMm ownExtent, List<(FacetFrame Frame, PlaneRectMm Extent)> coplanar, VolumeDetectionOptions tuning)
    {
        frame = own;
        extent = ownExtent;
        neighbours = coplanar;
        options = tuning;
    }

    /// <summary>The seams of one facet.</summary>
    /// <param name="frame">The facet's frame.</param>
    /// <param name="extent">Its extent.</param>
    /// <param name="others">The other facets with their extents.</param>
    /// <param name="options">Tuning (seam angle, offset, reach).</param>
    /// <returns>Its seams (none when no neighbour is near-coplanar).</returns>
    public static FacetSeams Of(
        FacetFrame frame, PlaneRectMm extent, IEnumerable<(FacetFrame Frame, PlaneRectMm Extent)> others, VolumeDetectionOptions options)
    {
        var minCos = Math.Cos(options.SeamMaxAngleDeg * Math.PI / 180);
        var coplanar = others.Where(o => Dot(frame.Normal, o.Frame.Normal) >= minCos).ToList();
        return new FacetSeams(frame, extent, coplanar, options);
    }

    /// <summary>
    /// Whether every extent edge whose band (<paramref name="band"/> mm) the plane point lies in is a seam at that
    /// point: just beyond the edge (by the seam reach) the wall goes on as a near-coplanar neighbour, within the
    /// seam offset of its plane and inside its extent.
    /// </summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <param name="band">The edge band, mm.</param>
    /// <returns>Whether the wall continues across each edge the point is near.</returns>
    public bool ContinuesAcross(double a, double b, double band)
    {
        var reach = options.SeamReachMm;
        var e = extent;
        return (a >= e.AMin + band || Continues(e.AMin - reach, b))
            && (a <= e.AMax - band || Continues(e.AMax + reach, b))
            && (b >= e.BMin + band || Continues(a, e.BMin - reach))
            && (b <= e.BMax - band || Continues(a, e.BMax + reach));
    }

    private static double Dot(double[] p, double[] q) => (p[0] * q[0]) + (p[1] * q[1]) + (p[2] * q[2]);

    private bool Continues(double a, double b)
    {
        var w = frame.ToWorld(a, b);
        foreach (var (f, e) in neighbours)
        {
            var (na, nb, nh) = FacetCloud.Local(f, w[0], w[1], w[2]);
            if (Math.Abs(nh) <= options.SeamMaxOffsetMm && na >= e.AMin && na <= e.AMax && nb >= e.BMin && nb <= e.BMax)
            {
                return true;
            }
        }

        return false;
    }
}
