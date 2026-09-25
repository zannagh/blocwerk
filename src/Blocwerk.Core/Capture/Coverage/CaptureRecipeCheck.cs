// <copyright file="CaptureRecipeCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Checks posed cameras against the walk-along capture recipe: a pass at knee height aimed up, one at chest height,
/// one overhead aimed down, an arc of about 70° around each end of the wall, and a semicircle under each volume.
/// Heights are measured from the floor band (the lowest facet edge), angles against gravity (the model's up).
/// </summary>
public static class CaptureRecipeCheck
{
    /// <summary>Views a pass needs to count as there.</summary>
    public const int MinViews = 3;

    /// <summary>An end arc needs at least this much of the ~70° (degrees around the wall's end).</summary>
    public const double MinArcDeg = 50;

    /// <summary>A volume's semicircle needs at least this much of its 180° (degrees around the volume, below it).</summary>
    public const double MinSemicircleDeg = 100;

    private const double KneeMaxMm = 800;
    private const double ChestMaxMm = 1700;
    private const double AimDeg = 10;
    private const double EndReachMm = 3000;

    /// <summary>Each recipe part with whether the cameras cover it.</summary>
    /// <param name="scene">The wall.</param>
    /// <param name="cameras">The posed cameras (the video frames', else the photos').</param>
    /// <param name="up">Unit up vector of the world.</param>
    /// <returns>The parts, volumes last.</returns>
    public static IReadOnlyList<RecipePass> Check(CoverageScene scene, IReadOnlyList<CoverageCamera> cameras, double[] up)
    {
        if (scene.Facets.Count == 0)
        {
            return [];
        }

        var floor = scene.Facets.SelectMany(f => f.Frame.Corners(f.Region)).Min(p => Dot(p, up));
        var poses = cameras.Select(c => (Height: Dot(c.Centre, up) - floor, Pitch: Deg(Math.Asin(Math.Clamp(Dot(c.Forward, up), -1, 1))))).ToList();
        var knee = poses.Count(p => p.Height <= KneeMaxMm && p.Pitch >= AimDeg);
        var chest = poses.Count(p => p.Height > KneeMaxMm && p.Height <= ChestMaxMm);
        var overhead = poses.Count(p => p.Height > ChestMaxMm && p.Pitch <= -AimDeg);
        var passes = new List<RecipePass>
        {
            Views("knee", "knee-height pass aimed up", knee),
            Views("chest", "chest-height pass", chest),
            Views("overhead", "overhead pass aimed down", overhead),
        };
        passes.AddRange(EndArcs(scene, cameras, up));
        passes.AddRange(scene.Volumes.Select(v => UnderVolume(scene, v, cameras)));
        return passes;
    }

    private static RecipePass Views(string key, string label, int views) =>
        new(key, label, views >= MinViews, string.Create(CultureInfo.InvariantCulture, $"{views} views"));

    private static IEnumerable<RecipePass> EndArcs(CoverageScene scene, IReadOnlyList<CoverageCamera> cameras, double[] up)
    {
        var reference = scene.Facets.OrderBy(f => Math.Abs(f.YawDeg)).ThenByDescending(f => f.Region.Area).First();
        var axis = Unit(Flat(reference.Frame.U, up));
        var outward = Unit(Flat(reference.Frame.Normal, up));
        if (axis is null)
        {
            yield break;
        }

        outward ??= Unit(Cross(up, axis));
        var corners = scene.Facets.SelectMany(f => f.Frame.Corners(f.Region)).ToList();
        var left = corners.MinBy(p => Dot(p, axis))!;
        var right = corners.MaxBy(p => Dot(p, axis))!;
        yield return Arc("arc-left", "arc of about 70° around the left end", left, axis, outward!, up, cameras, -1);
        yield return Arc("arc-right", "arc of about 70° around the right end", right, axis, outward!, up, cameras, 1);
    }

    private static RecipePass Arc(
        string key, string label, double[] end, double[] axis, double[] outward, double[] up, IReadOnlyList<CoverageCamera> cameras, int side)
    {
        var angles = new List<double>();
        foreach (var c in cameras)
        {
            var d = Flat([c.Centre[0] - end[0], c.Centre[1] - end[1], c.Centre[2] - end[2]], up);
            var along = Dot(d, axis) * side;
            var outwards = Dot(d, outward);
            if (Math.Sqrt(Dot(d, d)) <= EndReachMm && outwards > 0 && along > -EndReachMm / 2)
            {
                angles.Add(Deg(Math.Atan2(along, outwards)));
            }
        }

        var arc = angles.Count < MinViews ? 0 : angles.Max() - angles.Min();
        return new RecipePass(key, label, arc >= MinArcDeg, string.Create(CultureInfo.InvariantCulture, $"{arc:F0}° of arc from {angles.Count} views"));
    }

    private static RecipePass UnderVolume(CoverageScene scene, CoverageVolume volume, IReadOnlyList<CoverageCamera> cameras)
    {
        var frame = scene.Facet(volume.FacetId)!.Frame;
        var a = volume.Footprint.Count > 0 ? volume.Footprint.Average(p => p[0]) : 0;
        var b = volume.Footprint.Count > 0 ? volume.Footprint.Average(p => p[1]) : 0;
        var target = frame.ToWorld(a, b, volume.Surface.HeightAt(a, b) / 2);
        var angles = new List<double>();
        foreach (var c in cameras.Where(c => c.InFrame(target)))
        {
            var local = FacetCloud.Local(frame, c.Centre[0], c.Centre[1], c.Centre[2]);
            if (local.H > 0 && local.B < b - 100)
            {
                angles.Add(Deg(Math.Atan2(local.B - b, local.A - a)));
            }
        }

        var arc = angles.Count < MinViews ? 0 : angles.Max() - angles.Min();
        var key = string.Create(CultureInfo.InvariantCulture, $"under-volume-{volume.Index}");
        var label = string.Create(CultureInfo.InvariantCulture, $"semicircle under volume {volume.Index}");
        return new RecipePass(key, label, arc >= MinSemicircleDeg, string.Create(CultureInfo.InvariantCulture, $"{arc:F0}° from {angles.Count} views below it"));
    }

    private static double[] Flat(double[] v, double[] up)
    {
        var d = Dot(v, up);
        return [v[0] - (d * up[0]), v[1] - (d * up[1]), v[2] - (d * up[2])];
    }

    private static double[]? Unit(double[] v)
    {
        var len = Math.Sqrt(Dot(v, v));
        return len < 1e-6 ? null : [v[0] / len, v[1] / len, v[2] / len];
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double Deg(double rad) => rad * 180 / Math.PI;
}
