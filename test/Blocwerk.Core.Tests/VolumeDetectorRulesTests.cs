// <copyright file="VolumeDetectorRulesTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The detector on synthetic walls (<see cref="VolumeDetectorTests"/>' facet): a volume across the seam to a facet
/// that continues the wall, a low L-shaped plateau next to the pyramid, and a small bare box.
/// </summary>
public class VolumeDetectorRulesTests
{
    private static readonly PlaneRectMm Extent = VolumeDetectorTests.Extent;

    private static readonly Dictionary<string, List<KnownHoldEllipse>> NoHolds = [];

    private static readonly Dictionary<string, FacetFrame> Frames = new() { ["0"] = HoldFootprintEstimatorTests.Wall };

    private static readonly Dictionary<string, PlaneRectMm> Extents = new() { ["0"] = Extent };

    [Fact]
    public void VolumeAcrossASeam_IsKept_AtTheWallsEdgeRejected()
    {
        // A 800 × 500 mm pyramid (120 mm) on [2600, 3400]: facet 0 ends at a = 3000, facet 2 goes on in the same plane.
        var points = Wall(5000, (a, b) => Pyramid(a, b, 2600, 3400, 700, 1200));
        var frames = new Dictionary<string, FacetFrame> { ["0"] = HoldFootprintEstimatorTests.Wall, ["2"] = VolumeChecksTests.Continuation(proudMm: 0) };
        var extents = new Dictionary<string, PlaneRectMm> { ["0"] = Extent, ["2"] = new(0, 2000, 0, 2000) };

        var withSeam = VolumeDetector.Detect(points, frames, extents, NoHolds);
        var alone = VolumeDetector.Detect(points, Only(frames, "0"), Only(extents, "0"), NoHolds);

        var half = Assert.Single(withSeam, v => v.FacetId == "0" && v.IsAccepted);
        Assert.True(half.Footprint.Max(p => p.A) > Extent.AMax - 60);
        Assert.NotNull(half.Surface);
        Assert.Contains(alone, v => v.Status == "rejected:edge");
        Assert.DoesNotContain(alone, v => v.IsAccepted);
    }

    [Fact]
    public void LowLShapedPlateau_IsRejected_ThePyramidKept()
    {
        // Two 600 × 120 mm arms, 70 mm proud, meeting at (1900, 400): about half of their outline is raised.
        var points = Wall(Extent.AMax, (a, b) => VolumeDetectorTests.PyramidAt(a, b) + LShape(a, b));

        var found = VolumeDetector.Detect(points, Frames, Extents, NoHolds);

        var accepted = Assert.Single(found, v => v.IsAccepted);
        Assert.InRange(accepted.Footprint.Average(p => p.A), 1350, 1450);
        var l = Assert.Single(found, v => v.Status == "rejected:sparse");
        Assert.InRange(l.MedianHeightMm, 50, 80);
        Assert.Equal(2, VolumeDetector.Detect(points, Frames, Extents, NoHolds, new VolumeDetectionOptions { MinFillRatio = 0 }).Count(v => v.IsAccepted));
    }

    [Fact]
    public void SmallBareBox_IsAStep_OnlyWhenTheFacetHasLocatedHolds()
    {
        // A 220 × 220 mm box, 100 mm proud; the only located hold is far away.
        var points = Wall(Extent.AMax, (a, b) => a is > 2200 and < 2420 && b is > 400 and < 620 ? 100 : 0);
        var located = new Dictionary<string, List<KnownHoldEllipse>> { ["0"] = [new KnownHoldEllipse(500, 1500, 40, 40)] };

        Assert.Single(VolumeDetector.Detect(points, Frames, Extents, NoHolds), v => v.IsAccepted);
        Assert.Single(VolumeDetector.Detect(points, Frames, Extents, located), v => v.Status == "rejected:bare-step");
    }

    private static Dictionary<string, T> Only<T>(Dictionary<string, T> all, string id) => new() { [id] = all[id] };

    private static List<(float X, float Y, float Z)> Wall(double aMax, Func<double, double, double> height)
    {
        var rng = new Random(5);
        var points = new List<(float, float, float)>();
        for (var a = 5.0; a < aMax; a += 10)
        {
            for (var b = 5.0; b < Extent.BMax; b += 10)
            {
                var w = HoldFootprintEstimatorTests.Wall.ToWorld(a, b, height(a, b) + ((rng.NextDouble() - 0.5) * 16));
                points.Add(((float)w[0], (float)w[1], (float)w[2]));
            }
        }

        return points;
    }

    private static double Pyramid(double a, double b, double a0, double a1, double b0, double b1)
    {
        var inside = Math.Min(Math.Min(a - a0, a1 - a), Math.Min(b - b0, b1 - b));
        return inside <= 0 ? 0 : Math.Min(120, 0.8 * inside);
    }

    private static double LShape(double a, double b)
    {
        var horizontal = a is > 1900 and < 2500 && b is > 400 and < 520;
        var vertical = a is > 1900 and < 2020 && b is > 400 and < 1000;
        return horizontal || vertical ? 70 : 0;
    }
}
