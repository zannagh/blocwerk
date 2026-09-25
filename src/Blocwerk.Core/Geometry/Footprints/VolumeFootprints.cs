// <copyright file="VolumeFootprints.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>
/// Per-hold best views around volumes (<see cref="HoldFootprintRefiner"/>):
/// <list type="bullet">
/// <item>a hold placed ON a volume (<see cref="HoldVolumePlacement"/>) is traced on the volume's tangent plane at
/// its placement, not on the facet plane 100 mm behind it, so the capture views agree on it and the steep views that
/// look straight at a volume's underside count as frontal;</item>
/// <item>a view whose line of sight to the hold passes through a volume first does not see the hold (the hold is
/// under or behind the volume there) and is not traced.</item>
/// </list>
/// The tangent frame's axes match the 3D view's tilted hold frame (wall3d-holds.js <c>holdFrame</c>): x is the facet's
/// u projected onto the tangent plane, y = normal × x, so the stored outline draws there unchanged.
/// </summary>
public static class VolumeFootprints
{
    /// <summary>A view whose line of sight meets a volume this much before the hold is occluded, mm.</summary>
    public const double OcclusionMarginMm = 30;

    /// <summary>The tangent frame of a volume placement (origin at the placed point), or null for a degenerate normal.</summary>
    /// <param name="facet">The hold's facet.</param>
    /// <param name="p">The placement.</param>
    /// <returns>The frame.</returns>
    public static FacetFrame? TangentFrame(FacetFrame facet, HoldVolumePlacement p)
    {
        if (Unit(HoldVolumePlacer.WorldNormal(facet, p)) is not { } n)
        {
            return null;
        }

        var u = facet.U;
        var d = Dot(u, n);
        if (Unit([u[0] - (d * n[0]), u[1] - (d * n[1]), u[2] - (d * n[2])]) is not { } x)
        {
            return null;
        }

        double[] y = [(n[1] * x[2]) - (n[2] * x[1]), (n[2] * x[0]) - (n[0] * x[2]), (n[0] * x[1]) - (n[1] * x[0])];
        return new FacetFrame(HoldVolumePlacer.World(facet, p), x, y, n);
    }

    /// <summary>Where the line from <paramref name="from"/> through <paramref name="through"/> meets a plane, in its (a, b); null when parallel.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="from">A world point (a camera).</param>
    /// <param name="through">Another world point on the line.</param>
    /// <returns>The plane point.</returns>
    public static (double A, double B)? ToPlane(FacetFrame plane, double[] from, double[] through)
    {
        double[] d = [through[0] - from[0], through[1] - from[1], through[2] - from[2]];
        var den = Dot(d, plane.Normal);
        if (Math.Abs(den) < 1e-9)
        {
            return null;
        }

        var t = (Dot(plane.Origin, plane.Normal) - Dot(from, plane.Normal)) / den;
        double[] p = [from[0] + (t * d[0]) - plane.Origin[0], from[1] + (t * d[1]) - plane.Origin[1], from[2] + (t * d[2]) - plane.Origin[2]];
        return (Dot(p, plane.U), Dot(p, plane.V));
    }

    /// <summary>Whether a volume stands between the camera and the hold's point.</summary>
    /// <param name="camera">The camera centre, world mm.</param>
    /// <param name="target">The hold's point, world mm.</param>
    /// <param name="volumes">The hold's facet with its volumes.</param>
    /// <returns>True when occluded.</returns>
    public static bool Occluded(double[] camera, double[] target, FacetVolumes volumes)
    {
        var from = FacetCloud.Local(volumes.Facet, camera[0], camera[1], camera[2]);
        var at = FacetCloud.Local(volumes.Facet, target[0], target[1], target[2]);
        if (from.H <= at.H + 1)
        {
            return false;
        }

        // The line of sight, continued to the facet plane: where the volume's ray walk starts from.
        var t = from.H / (from.H - at.H);
        double a = from.A + ((at.A - from.A) * t), b = from.B + ((at.B - from.B) * t);
        foreach (var v in volumes.Volumes)
        {
            if (v.RayHit(from, a, b, 5) is { } hit && hit.H > at.H + OcclusionMarginMm * 0.5
                && Length(hit.A - at.A, hit.B - at.B, hit.H - at.H) > OcclusionMarginMm)
            {
                return true;
            }
        }

        return false;
    }

    private static double[]? Unit(double[] v)
    {
        var len = Math.Sqrt(Dot(v, v));
        return len < 1e-9 ? null : [v[0] / len, v[1] / len, v[2] / len];
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double Length(double x, double y, double z) => Math.Sqrt((x * x) + (y * y) + (z * z));
}
