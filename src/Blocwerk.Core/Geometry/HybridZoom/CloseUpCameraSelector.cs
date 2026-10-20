// <copyright file="CloseUpCameraSelector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Registration;

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>
/// Picks the capture photo that shows a zoomed panel view best. The view is sampled on a grid of wall points
/// (<see cref="GridSteps"/>²); a photo qualifies when every sample projects into it clear of its edges, it looks
/// at the facet from the front side within <see cref="CloseUpRules.MinFrontality"/>, one homography explains its
/// lens over the crop, and it resolves the wall at least <see cref="CloseUpRules.MinGain"/> times as finely as the
/// panel photo. Qualifying photos rank by resolution × frontality × relative sharpness. Pure: no I/O.
/// </summary>
public static class CloseUpCameraSelector
{
    /// <summary>Samples per side of the view grid.</summary>
    public const int GridSteps = 5;

    /// <summary>Relative sharpness never moves a photo's score by more than this range.</summary>
    private const double MinSharpFactor = 0.6;

    private const double MaxSharpFactor = 1.2;

    /// <summary>
    /// Every qualifying photo, best first; empty when none beats the panel photo or the view cannot be mapped.
    /// </summary>
    /// <param name="worldAt">The wall point (world mm) at (u, v) of the view, or null where it is unmappable.</param>
    /// <param name="normal">The facet's unit normal (out of the wall).</param>
    /// <param name="panelPxPerMm">How finely the panel photo resolves the view, px per mm.</param>
    /// <param name="photos">The candidate photos.</param>
    /// <param name="rules">The thresholds.</param>
    /// <returns>The ranking.</returns>
    public static IReadOnlyList<CloseUpChoice> Rank(
        Func<double, double, double[]?> worldAt,
        double[] normal,
        double panelPxPerMm,
        IReadOnlyList<CloseUpPhoto> photos,
        CloseUpRules rules)
    {
        var samples = Sample(worldAt);
        if (samples is null || !(panelPxPerMm > 0))
        {
            return [];
        }

        var planeArea = QuadArea3D(Corners(samples).Select(s => s.World).ToList());
        if (!(planeArea > 1e-6))
        {
            return [];
        }

        var median = MedianSharpness(photos);
        var centre = samples[samples.Count / 2].World;
        var result = new List<CloseUpChoice>();
        foreach (var photo in photos)
        {
            if (Judge(photo, samples, centre, normal, planeArea, panelPxPerMm, median, rules) is { } choice)
            {
                result.Add(choice);
            }
        }

        return [.. result.OrderByDescending(c => c.Score).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>Pixels per millimetre of a quad: √(image area ÷ wall area).</summary>
    /// <param name="image">The quad's four corners in image px, in order.</param>
    /// <param name="world">The same corners on the wall, world mm.</param>
    /// <returns>The resolution, or 0 when degenerate.</returns>
    public static double PxPerMm(IReadOnlyList<(double X, double Y)> image, IReadOnlyList<double[]> world)
    {
        var plane = QuadArea3D(world);
        return plane > 1e-9 ? Math.Sqrt(Shoelace(image) / plane) : 0;
    }

    private static CloseUpChoice? Judge(
        CloseUpPhoto photo,
        List<(double U, double V, double[] World)> samples,
        double[] centre,
        double[] normal,
        double planeArea,
        double panelPxPerMm,
        double? medianSharpness,
        CloseUpRules rules)
    {
        var cam = photo.Camera;
        var toCamera = Vec3.Sub(cam.Centre, centre);
        var distance = Vec3.Length(toCamera);
        var frontality = distance > 0 ? Vec3.Dot(toCamera, normal) / distance : 0;
        if (frontality < rules.MinFrontality)
        {
            return null;
        }

        var projected = Project(cam, samples, rules.EdgeMargin);
        if (projected is null)
        {
            return null;
        }

        var corners = Corners(samples.Zip(projected, (s, p) => (s.U, s.V, P: p)).ToList()).Select(c => c.P).ToList();
        var pxPerMm = Math.Sqrt(Shoelace(corners) / planeArea);
        var gain = pxPerMm / panelPxPerMm;
        if (!(gain >= rules.MinGain))
        {
            return null;
        }

        var bounds = (projected.Min(p => p.X), projected.Min(p => p.Y), projected.Max(p => p.X), projected.Max(p => p.Y));
        var warp = CloseUpWarp.Fit(samples.Select(s => (s.U, s.V)).ToList(), projected);
        var longSide = Math.Max(bounds.Item3 - bounds.Item1, bounds.Item4 - bounds.Item2);
        if (warp is null || warp.Value.ResidualPx > rules.MaxResidualShare * longSide)
        {
            return null;
        }

        var score = pxPerMm * frontality * SharpFactor(photo.Sharpness, medianSharpness);
        return new CloseUpChoice(photo.Name, warp.Value.ViewToPhoto, bounds, pxPerMm, gain, frontality, warp.Value.ResidualPx, score);
    }

    /// <summary>The samples' pixels, or null when one is behind the camera, past its lens model or near an edge.</summary>
    private static List<(double X, double Y)>? Project(
        Footprints.SolvedCamera cam, List<(double U, double V, double[] World)> samples, double margin)
    {
        double mx = cam.Width * margin, my = cam.Height * margin;
        var result = new List<(double X, double Y)>(samples.Count);
        foreach (var s in samples)
        {
            if (cam.Project(s.World) is not { } p || p.X < mx || p.Y < my || p.X > cam.Width - mx || p.Y > cam.Height - my)
            {
                return null;
            }

            result.Add(p);
        }

        return result;
    }

    /// <summary>The view grid, row by row (v outer, u inner); null when any point is unmappable.</summary>
    private static List<(double U, double V, double[] World)>? Sample(Func<double, double, double[]?> worldAt)
    {
        var result = new List<(double U, double V, double[] World)>(GridSteps * GridSteps);
        for (var j = 0; j < GridSteps; j++)
        {
            for (var i = 0; i < GridSteps; i++)
            {
                double u = (double)i / (GridSteps - 1), v = (double)j / (GridSteps - 1);
                if (worldAt(u, v) is not { Length: 3 } w || !w.All(double.IsFinite))
                {
                    return null;
                }

                result.Add((u, v, w));
            }
        }

        return result;
    }

    /// <summary>The grid's corners in quad order: (0,0), (1,0), (1,1), (0,1).</summary>
    private static List<T> Corners<T>(List<T> grid)
    {
        const int Last = GridSteps - 1;
        return [grid[0], grid[Last], grid[(Last * GridSteps) + Last], grid[Last * GridSteps]];
    }

    private static double? MedianSharpness(IReadOnlyList<CloseUpPhoto> photos)
    {
        var values = photos.Select(p => p.Sharpness).OfType<double>().Where(s => s > 0).Order().ToList();
        return values.Count == 0 ? null : values[values.Count / 2];
    }

    private static double SharpFactor(double? sharpness, double? median) =>
        sharpness is > 0 && median is > 0 ? Math.Clamp(sharpness.Value / median.Value, MinSharpFactor, MaxSharpFactor) : 1;

    private static double Shoelace(IReadOnlyList<(double X, double Y)> p)
    {
        var sum = 0.0;
        for (var i = 0; i < p.Count; i++)
        {
            var q = p[(i + 1) % p.Count];
            sum += (p[i].X * q.Y) - (q.X * p[i].Y);
        }

        return Math.Abs(sum) / 2;
    }

    private static double QuadArea3D(IReadOnlyList<double[]> q) =>
        TriangleArea(q[0], q[1], q[2]) + TriangleArea(q[0], q[2], q[3]);

    private static double TriangleArea(double[] a, double[] b, double[] c)
    {
        double[] ab = Vec3.Sub(b, a), ac = Vec3.Sub(c, a);
        double[] cross = [(ab[1] * ac[2]) - (ab[2] * ac[1]), (ab[2] * ac[0]) - (ab[0] * ac[2]), (ab[0] * ac[1]) - (ab[1] * ac[0])];
        return Vec3.Length(cross) / 2;
    }
}
