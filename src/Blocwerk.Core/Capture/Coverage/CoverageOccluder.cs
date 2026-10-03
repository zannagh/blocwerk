// <copyright file="CoverageOccluder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// One facet as a blocker of lines of sight, only where it really is: its region rectangle cut by its seams
/// (<see cref="CoverageOccluderSeams"/>), never its infinite plane. A line of sight is blocked when it crosses the
/// facet's plane inside that shape (as the wall textures' <c>occlusion.py</c>).
/// </summary>
internal sealed class CoverageOccluder
{
    /// <summary>A board edge this close to the crossing does not block (neighbouring facets share their edges), mm.</summary>
    public const double EdgeMarginMm = 60;

    /// <summary>A crossing this close to the target is the target's own surroundings, mm.</summary>
    public const double NearTargetMm = 80;

    /// <summary>A point at least this far behind the facet's shape is inside the wall, mm.</summary>
    public const double InsideWallDepthMm = 30;

    /// <summary>A board facing more than ~120° away from the point's own surface is the far side of a structure, not the wall around it.</summary>
    private const double MinFacingCos = -0.5;

    private readonly (double Alpha, double Beta, double Gamma)[] halfPlanes;
    private readonly double[] boxMin;
    private readonly double[] boxMax;

    /// <summary>Initializes a new instance of the <see cref="CoverageOccluder"/> class.</summary>
    /// <param name="facet">The facet.</param>
    /// <param name="cuts">Its seam cuts: (a, b) is kept where alpha·a + beta·b ≥ gamma.</param>
    public CoverageOccluder(CoverageFacet facet, IReadOnlyList<(double Alpha, double Beta, double Gamma)> cuts)
    {
        Facet = facet;
        halfPlanes = [.. cuts];
        var corners = facet.Frame.Corners(facet.Region);
        boxMin = [.. Enumerable.Range(0, 3).Select(i => corners.Min(c => c[i]) - 1)];
        boxMax = [.. Enumerable.Range(0, 3).Select(i => corners.Max(c => c[i]) + 1)];
    }

    /// <summary>The facet.</summary>
    public CoverageFacet Facet { get; }

    /// <summary>Whether (a, b) lies in the facet's shape, shrunk by <paramref name="inset"/>.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <param name="inset">Inset, mm.</param>
    /// <returns>True inside.</returns>
    public bool Contains(double a, double b, double inset = 0)
    {
        var r = Facet.Region;
        return a >= r.AMin + inset && a <= r.AMax - inset && b >= r.BMin + inset && b <= r.BMax - inset && WithinSeams(a, b, inset);
    }

    /// <summary>Whether (a, b) lies on the kept side of every seam cut, by at least <paramref name="inset"/>.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <param name="inset">Inset, mm.</param>
    /// <returns>True on the kept side.</returns>
    public bool WithinSeams(double a, double b, double inset = 0)
    {
        foreach (var (alpha, beta, gamma) in halfPlanes)
        {
            if ((alpha * a) + (beta * b) < gamma + inset)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the straight line of sight from the camera to the target passes through the facet's shape.</summary>
    /// <param name="camera">Camera centre, world mm.</param>
    /// <param name="target">The target, world mm.</param>
    /// <returns>True when blocked.</returns>
    public bool Blocks(double[] camera, double[] target)
    {
        for (var i = 0; i < 3; i++)
        {
            if (Math.Max(camera[i], target[i]) < boxMin[i] || Math.Min(camera[i], target[i]) > boxMax[i])
            {
                return false;
            }
        }

        var from = FacetCloud.Local(Facet.Frame, camera[0], camera[1], camera[2]);
        var to = FacetCloud.Local(Facet.Frame, target[0], target[1], target[2]);
        if (Math.Sign(from.H) == Math.Sign(to.H) || from.H == to.H)
        {
            return false;
        }

        var t = from.H / (from.H - to.H);
        var length = Math.Sqrt(Sq(to.A - from.A) + Sq(to.B - from.B) + Sq(to.H - from.H));
        if ((1 - t) * length < NearTargetMm)
        {
            return false;
        }

        return Contains(from.A + (t * (to.A - from.A)), from.B + (t * (to.B - from.B)), EdgeMarginMm);
    }

    /// <summary>
    /// Whether the point lies behind the facet's shape (at least <see cref="InsideWallDepthMm"/> behind its plane,
    /// within its shape shrunk by <see cref="EdgeMarginMm"/>) and the facet does not face away from the point's surface.
    /// </summary>
    /// <param name="point">The point, world mm.</param>
    /// <param name="normal">The point's outward unit normal, world.</param>
    /// <returns>True when the point is inside the wall behind this facet.</returns>
    public bool Behind(double[] point, double[] normal)
    {
        var n = Facet.Frame.Normal;
        if ((n[0] * normal[0]) + (n[1] * normal[1]) + (n[2] * normal[2]) < MinFacingCos)
        {
            return false;
        }

        var local = FacetCloud.Local(Facet.Frame, point[0], point[1], point[2]);
        return local.H <= -InsideWallDepthMm && Contains(local.A, local.B, EdgeMarginMm);
    }

    private static double Sq(double x) => x * x;
}
