// <copyright file="VolumeDetectorSplitTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The detector's second look at a merged region (a volume joined by a thin strand to bumps at the facet's edge is cut
/// out and kept) and its rejection of low regions that read as several separate peaks.
/// </summary>
public class VolumeDetectorSplitTests
{
    private static readonly PlaneRectMm Extent = VolumeDetectorTests.Extent;

    private static readonly Dictionary<string, List<KnownHoldEllipse>> NoHolds = [];

    private static readonly Dictionary<string, FacetFrame> Frames = new() { ["0"] = HoldFootprintEstimatorTests.Wall };

    private static readonly Dictionary<string, PlaneRectMm> Extents = new() { ["0"] = Extent };

    [Fact]
    public void PyramidJoinedToTheEdgeByAStrand_IsCutOutAndKept()
    {
        // A 500 × 500 mm pyramid (120 mm) on [1400, 1900] × [900, 1400]; a 40 mm wide, 80 mm high strand runs up from
        // its top to a 300 × 250 mm bump field (60 mm) that reaches the facet's top edge (b = 2000).
        var points = Wall((a, b) => Math.Max(Pyramid(a, b, 1400, 1900, 900, 1400), Strand(a, b)));

        var found = VolumeDetector.Detect(points, Frames, Extents, NoHolds);

        var pyramid = Assert.Single(found, v => v.IsAccepted);
        Assert.InRange(pyramid.Footprint.Average(p => p.A), 1550, 1750);
        Assert.True(pyramid.Footprint.Max(p => p.B) < 1500, "the strand and the bumps above are cut off");
        Assert.NotNull(pyramid.Surface);
        Assert.DoesNotContain(found, v => v.IsAccepted && v.Footprint.Max(p => p.B) > Extent.BMax - 60);
    }

    [Fact]
    public void AloneAtTheEdge_IsStillRejected()
    {
        var points = Wall(Strand);

        var found = VolumeDetector.Detect(points, Frames, Extents, NoHolds);

        Assert.DoesNotContain(found, v => v.IsAccepted);
    }

    [Fact]
    public void LowRegionOfSeveralPeaks_IsACluster_TallOrSinglePeakIsNot()
    {
        var options = new VolumeDetectionOptions();
        var (peaks, peaksRing) = SyntheticVolumes.Detected(SyntheticVolumes.TwoPeaks, 1000, 1750, 800, 1150, seed: 3);
        var (pyramid, pyramidRing) = SyntheticVolumes.Detected(SyntheticVolumes.Pyramid, 1200, 1600, 800, 1150);
        DetectedVolume Measured(List<(double A, double B)> ring, double h90) => new("0", ring, 0.1, h90, h90 / 2, 500, 0, 0, 0.8, string.Empty, null);

        Assert.True(VolumeDetector.IsLowMultiPeak(Measured(peaksRing, 60), peaks, options));
        Assert.False(VolumeDetector.IsLowMultiPeak(Measured(peaksRing, 120), peaks, options));
        Assert.False(VolumeDetector.IsLowMultiPeak(Measured(pyramidRing, 60), pyramid, options));
    }

    private static double Strand(double a, double b)
    {
        var strand = a is > 1630 and < 1670 && b is >= 1350 and < 1760;
        var bumps = a is > 1500 and < 1800 && b >= 1750 ? 60 + (15 * Math.Sin(a / 25) * Math.Sin(b / 25)) : 0;
        return Math.Max(strand ? 80 : 0, bumps);
    }

    private static List<(float X, float Y, float Z)> Wall(Func<double, double, double> height)
    {
        var rng = new Random(5);
        var points = new List<(float, float, float)>();
        for (var a = 5.0; a < Extent.AMax; a += 10)
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
}
