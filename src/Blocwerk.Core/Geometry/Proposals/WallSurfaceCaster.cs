// <copyright file="WallSurfaceCaster.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>
/// Lifts a detection from its photo onto the wall: the pixel's ray through the solved camera to the nearest
/// facet plane hit inside that facet's extent, walked back onto a volume standing on the facet when it meets
/// one first. Grazing views (the facet seen under more than <see cref="MinCosView"/>) are dropped: their boxes
/// are mostly the hold's side and their hits smear along the wall.
/// </summary>
public static class WallSurfaceCaster
{
    /// <summary>Lowest cosine between a ray and the facet normal (≈ 66°).</summary>
    public const double MinCosView = 0.4;

    private const double ExtentMarginMm = 20;

    /// <summary>The hit of one detection, or null (sky, floor, mats, grazing).</summary>
    /// <param name="camera">The photo's solved camera.</param>
    /// <param name="d">The detection.</param>
    /// <param name="facets">The model's facets.</param>
    /// <returns>The hit or null.</returns>
    public static SurfaceHit? Cast(SolvedCamera camera, CaptureDetection d, IReadOnlyList<CastFacet> facets)
    {
        var o = camera.Centre;
        var dir = camera.RayDirection(d.Px, d.Py);
        (CastFacet Facet, double T, double Cos)? best = null;
        foreach (var f in facets)
        {
            var n = f.Frame.Normal;
            var den = Dot(dir, n);
            if (-den < MinCosView)
            {
                continue;
            }

            var t = Dot(Sub(f.Frame.Origin, o), n) / den;
            var (a, b, _) = FacetCloud.Local(f.Frame, o[0] + (t * dir[0]), o[1] + (t * dir[1]), o[2] + (t * dir[2]));
            var e = f.Extent;
            var inside = a >= e.AMin - ExtentMarginMm && a <= e.AMax + ExtentMarginMm && b >= e.BMin - ExtentMarginMm && b <= e.BMax + ExtentMarginMm;
            if (t > 0 && inside && (best is null || t < best.Value.T))
            {
                best = (f, t, -den);
            }
        }

        return best is { } hit ? OnSurface(camera, d, o, dir, hit.Facet, hit.T, hit.Cos) : null;
    }

    private static SurfaceHit OnSurface(SolvedCamera camera, CaptureDetection d, double[] o, double[] dir, CastFacet f, double t, double cos)
    {
        double[] p = [o[0] + (t * dir[0]), o[1] + (t * dir[1]), o[2] + (t * dir[2])];
        var (a, b, _) = FacetCloud.Local(f.Frame, p[0], p[1], p[2]);
        var from = FacetCloud.Local(f.Frame, o[0], o[1], o[2]);
        var h = 0.0;
        foreach (var v in f.Volumes)
        {
            if (v.RayHit(from, a, b, 5) is { } onVolume && onVolume.H > h)
            {
                (a, b, h) = onVolume;
            }
        }

        var world = f.Frame.ToWorld(a, b, h);
        var dist = Math.Sqrt(Dot(Sub(world, o), Sub(world, o)));
        var size = 2 * d.RadiusPx * dist / camera.K[0];
        return new SurfaceHit(d, f.Id, a, b, h, world, o, dir, size, cos);
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
}
