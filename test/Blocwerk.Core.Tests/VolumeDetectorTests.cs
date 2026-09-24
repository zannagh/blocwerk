// <copyright file="VolumeDetectorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Synthetic splat surfaces on the vertical test facet (a along +x, b up, normal −y): a noisy wall with a
/// 400 × 300 mm truncated pyramid (120 mm) and a big dome-shaped hold (a macro). The detector must find the
/// volume, shape it, and reject the macro through its known hold outline.
/// </summary>
public class VolumeDetectorTests
{
    internal static readonly PlaneRectMm Extent = new(0, 3000, 0, 2000);

    private static readonly Dictionary<string, FacetFrame> Frames = new() { ["0"] = HoldFootprintEstimatorTests.Wall };

    private static readonly Dictionary<string, PlaneRectMm> Extents = new() { ["0"] = Extent };

    private static readonly Dictionary<string, List<KnownHoldEllipse>> NoHolds = [];

    /// <summary>The pyramid's true height at (a, b): 0.8 mm per mm in from its rim, capped at 120.</summary>
    public static double PyramidAt(double a, double b)
    {
        var inside = Math.Min(Math.Min(a - 1200, 1600 - a), Math.Min(b - 800, 1100 - b));
        return inside <= 0 ? 0 : Math.Min(120, 0.8 * inside);
    }

    /// <summary>The scene's points (world mm): wall every 10 mm with ±8 mm noise, the pyramid and the macro on it.</summary>
    public static List<(float X, float Y, float Z)> Scene(bool withMacro = true)
    {
        var rng = new Random(7);
        var points = new List<(float, float, float)>();
        for (var a = 5.0; a < Extent.AMax; a += 10)
        {
            for (var b = 5.0; b < Extent.BMax; b += 10)
            {
                var h = PyramidAt(a, b) + Macro(a, b, withMacro) + ((rng.NextDouble() - 0.5) * 16);
                var w = HoldFootprintEstimatorTests.Wall.ToWorld(a, b, h);
                points.Add(((float)w[0], (float)w[1], (float)w[2]));
            }
        }

        return points;
    }

    [Fact]
    public void FindsThePyramid_AndRejectsTheMacro()
    {
        var holds = new Dictionary<string, List<KnownHoldEllipse>> { ["0"] = [new KnownHoldEllipse(600, 600, 190, 160)] };

        var found = VolumeDetector.Detect(Scene(), Frames, Extents, holds);

        var accepted = Assert.Single(found, v => v.IsAccepted);

        // The raised cells (> 35 mm) are the pyramid minus its low rim; the surface covers the rim again.
        Assert.InRange(accepted.AreaM2, 0.05, 0.14);
        Assert.InRange(accepted.HeightMm, 85, 135);
        Assert.InRange(accepted.Footprint.Average(p => p.A), 1350, 1450);
        Assert.Contains(found, v => v.Status == "rejected:single-hold");
        var surface = accepted.Surface!;
        Assert.InRange(surface.HeightAt(1400, 950), 105, 135);
        Assert.InRange(surface.HeightAt(1240, 950), 15, 60);
        Assert.Equal(0, surface.HeightAt(1000, 950));
    }

    [Fact]
    public void FlatWall_HasNoVolumes()
    {
        var rng = new Random(3);
        var points = new List<(float, float, float)>();
        for (var a = 5.0; a < Extent.AMax; a += 10)
        {
            for (var b = 5.0; b < Extent.BMax; b += 10)
            {
                var w = HoldFootprintEstimatorTests.Wall.ToWorld(a, b, (rng.NextDouble() - 0.5) * 24);
                points.Add(((float)w[0], (float)w[1], (float)w[2]));
            }
        }

        Assert.DoesNotContain(VolumeDetector.Detect(points, Frames, Extents, NoHolds), v => v.IsAccepted);
    }

    [Fact]
    public void TallThingInFrontOfTheWall_IsNotAVolume()
    {
        // A 400 mm tall block (a mat, a person) is far taller than any volume.
        var points = Scene(withMacro: false).Select(p =>
        {
            var (a, b, h) = FacetCloud.Local(HoldFootprintEstimatorTests.Wall, p.X, p.Y, p.Z);
            var w = HoldFootprintEstimatorTests.Wall.ToWorld(a, b, h > 60 ? h + 300 : h);
            return ((float)w[0], (float)w[1], (float)w[2]);
        }).ToList();

        var found = VolumeDetector.Detect(points, Frames, Extents, NoHolds);

        Assert.DoesNotContain(found, v => v.IsAccepted);
    }

    [Fact]
    public void Surface_SurvivesItsStorageForm()
    {
        var surface = new VolumeSurface(new CellGrid(100, 200, 20, 3, 2), [0, 50, 0, 10, 120, 7]);

        var back = VolumeSurface.FromJson(surface.ToJson())!;

        Assert.Equal(surface.Grid, back.Grid);
        Assert.Equal(surface.Heights, back.Heights);
        Assert.Equal(120, back.HeightAt(130, 230));
        Assert.Null(VolumeSurface.FromJson("{\"version\":2}"));
        Assert.Null(VolumeSurface.FromJson("not json"));
    }

    private static double Macro(double a, double b, bool on)
    {
        double da = (a - 600) / 180, db = (b - 600) / 150;
        var r2 = (da * da) + (db * db);
        return on && r2 < 1 ? 70 * Math.Sqrt(1 - r2) : 0;
    }
}
