// <copyright file="FacetCloud.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// One facet's evidence points in its own frame: (a, b) on the plane and the height above the wall's fitted
/// surface (<see cref="WallSurfaceFit"/>). Points on another facet's surface (inside its extent and nearer its
/// plane) are left out, so a neighbouring facet at a fold or a step never reads as a volume.
/// </summary>
public sealed class FacetCloud
{
    private const double MinHeightMm = -150;
    private const double MaxHeightMm = 600;

    private FacetCloud(string facetId, FacetFrame frame, PlaneRectMm extent, double[] a, double[] b, double[] h)
    {
        FacetId = facetId;
        Frame = frame;
        Extent = extent;
        A = a;
        B = b;
        H = h;
    }

    /// <summary>The facet.</summary>
    public string FacetId { get; }

    /// <summary>Its frame.</summary>
    public FacetFrame Frame { get; }

    /// <summary>Its extent on the plane.</summary>
    public PlaneRectMm Extent { get; }

    /// <summary>Along u, mm.</summary>
    public double[] A { get; }

    /// <summary>Along v, mm.</summary>
    public double[] B { get; }

    /// <summary>Height above the wall surface, mm.</summary>
    public double[] H { get; }

    /// <summary>Number of points.</summary>
    public int Count => A.Length;

    /// <summary>Builds the cloud of one facet.</summary>
    /// <param name="points">World points, mm.</param>
    /// <param name="facetId">The facet.</param>
    /// <param name="frame">Its frame.</param>
    /// <param name="extent">Its extent.</param>
    /// <param name="others">The other facets with their extents.</param>
    /// <param name="otherSurfaceMm">How near another facet's plane a point must be to belong to it.</param>
    /// <returns>The cloud (maybe empty).</returns>
    public static FacetCloud Build(
        IReadOnlyList<(float X, float Y, float Z)> points,
        string facetId,
        FacetFrame frame,
        PlaneRectMm extent,
        IReadOnlyList<(FacetFrame Frame, PlaneRectMm Extent)> others,
        double otherSurfaceMm)
    {
        var a = new List<double>();
        var b = new List<double>();
        var h = new List<double>();
        foreach (var (x, y, z) in points)
        {
            var (pa, pb, ph) = Local(frame, x, y, z);
            if (pa <= extent.AMin || pa >= extent.AMax || pb <= extent.BMin || pb >= extent.BMax || ph <= MinHeightMm || ph >= MaxHeightMm)
            {
                continue;
            }

            if (OnOther(x, y, z, Math.Abs(ph), others, otherSurfaceMm))
            {
                continue;
            }

            a.Add(pa);
            b.Add(pb);
            h.Add(ph);
        }

        var relative = h.Count > 0 ? WallSurfaceFit.Relative(a, b, h) : [];
        return new FacetCloud(facetId, frame, extent, [.. a], [.. b], relative);
    }

    /// <summary>A world point in a facet's (a, b, height) frame.</summary>
    /// <param name="frame">The facet.</param>
    /// <param name="x">World x.</param>
    /// <param name="y">World y.</param>
    /// <param name="z">World z.</param>
    /// <returns>The local coordinates.</returns>
    public static (double A, double B, double H) Local(FacetFrame frame, double x, double y, double z)
    {
        double rx = x - frame.Origin[0], ry = y - frame.Origin[1], rz = z - frame.Origin[2];
        return (
            (rx * frame.U[0]) + (ry * frame.U[1]) + (rz * frame.U[2]),
            (rx * frame.V[0]) + (ry * frame.V[1]) + (rz * frame.V[2]),
            (rx * frame.Normal[0]) + (ry * frame.Normal[1]) + (rz * frame.Normal[2]));
    }

    private static bool OnOther(
        double x, double y, double z, double ownMm, IReadOnlyList<(FacetFrame Frame, PlaneRectMm Extent)> others, double tolMm)
    {
        foreach (var (f, e) in others)
        {
            var (a, b, h) = Local(f, x, y, z);
            var d = Math.Abs(h);
            if (d < tolMm && d < ownMm && a >= e.AMin && a <= e.AMax && b >= e.BMin && b <= e.BMax)
            {
                return true;
            }
        }

        return false;
    }
}
