// <copyright file="HoldVolumeRemap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Moves a hold that sits on a volume to where it really is. Its plane position was mapped FLAT onto the
/// facet, i.e. where the photo ray through the hold hits the facet plane; the hold itself sits where that
/// ray first meets the volume. Following the volumes prototype (<c>.me/plan-next-multiview-holds.md</c>
/// item 4), the ray is taken from the most frontal solved capture camera, and the volume is the splat's
/// own surface (a height field over the facet) instead of a fitted polyhedron. Auto, unreviewed: on The
/// Attic the moved positions match the hold's colour in the splat better for 21 of 38 holds, worse for 6.
/// </summary>
public static class HoldVolumeRemap
{
    /// <summary>A move longer than this is not trusted (the ray grazed something else).</summary>
    public const double MaxShiftMm = 250;

    private const double StepMm = 2;
    private const double StartHeightMm = 400;
    private const double SurfaceRadiusMm = 15;
    private const int MinSurfacePoints = 3;
    private const double MinCameraOffsetMm = 300;
    private const double MaxCameraDistanceMm = 7000;

    /// <summary>The (a, b) shift from the hold's plane centre to where the frontal ray meets the surface; null when none.</summary>
    /// <param name="a">Hold plane centre along u, mm.</param>
    /// <param name="b">Hold plane centre along v, mm.</param>
    /// <param name="frame">The hold's facet.</param>
    /// <param name="grid">The facet's splat points as (a, b, height), bucketed by <paramref name="cellMm"/>.</param>
    /// <param name="cellMm">The grid's cell size.</param>
    /// <param name="cameras">Solved capture camera centres, world mm.</param>
    /// <returns>The shift, or null.</returns>
    public static (double A, double B)? Shift(
        double a,
        double b,
        FacetFrame frame,
        Dictionary<(int I, int J), List<(double A, double B, double D)>> grid,
        double cellMm,
        IReadOnlyList<double[]> cameras)
    {
        var camera = Frontal(a, b, frame, cameras);
        if (camera is null)
        {
            return null;
        }

        // The ray from the camera to the flat point, in facet coordinates: (a, b, height).
        var (ca, cb, ch) = camera.Value;
        var length = Math.Sqrt(((a - ca) * (a - ca)) + ((b - cb) * (b - cb)) + (ch * ch));
        var t0 = Math.Max(0, (ch - StartHeightMm) / ch);
        for (var t = t0; t <= 1; t += StepMm / length)
        {
            double pa = ca + (t * (a - ca)), pb = cb + (t * (b - cb)), ph = ch * (1 - t);
            var surface = SurfaceAt(grid, cellMm, pa, pb);
            if (surface is { } s && ph <= s)
            {
                var shift = (A: pa - a, B: pb - b);
                return Math.Sqrt((shift.A * shift.A) + (shift.B * shift.B)) <= MaxShiftMm ? shift : null;
            }
        }

        return null;
    }

    /// <summary>The most frontal camera in front of the point, in facet coordinates; null when none.</summary>
    private static (double A, double B, double H)? Frontal(double a, double b, FacetFrame frame, IReadOnlyList<double[]> cameras)
    {
        (double A, double B, double H)? best = null;
        var bestCos = -1.0;
        var x = frame.ToWorld(a, b);
        foreach (var c in cameras)
        {
            double dx = c[0] - x[0], dy = c[1] - x[1], dz = c[2] - x[2];
            var dist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var h = Dot(frame.Normal, dx, dy, dz);
            if (h < MinCameraOffsetMm || dist > MaxCameraDistanceMm || h / dist <= bestCos)
            {
                continue;
            }

            bestCos = h / dist;
            best = (a + Dot(frame.U, dx, dy, dz), b + Dot(frame.V, dx, dy, dz), h);
        }

        return best;
    }

    /// <summary>75th percentile height of the points within <see cref="SurfaceRadiusMm"/>; null when too few.</summary>
    private static double? SurfaceAt(Dictionary<(int I, int J), List<(double A, double B, double D)>> grid, double cellMm, double a, double b)
    {
        var i = (int)Math.Floor(a / cellMm);
        var j = (int)Math.Floor(b / cellMm);
        var heights = new List<double>();
        for (var di = -1; di <= 1; di++)
        {
            for (var dj = -1; dj <= 1; dj++)
            {
                if (!grid.TryGetValue((i + di, j + dj), out var list))
                {
                    continue;
                }

                foreach (var p in list)
                {
                    if (((p.A - a) * (p.A - a)) + ((p.B - b) * (p.B - b)) <= SurfaceRadiusMm * SurfaceRadiusMm)
                    {
                        heights.Add(p.D);
                    }
                }
            }
        }

        if (heights.Count < MinSurfacePoints)
        {
            return null;
        }

        heights.Sort();
        return heights[(int)Math.Floor(0.75 * (heights.Count - 1))];
    }

    private static double Dot(double[] v, double x, double y, double z) => (v[0] * x) + (v[1] * y) + (v[2] * z);
}
