// <copyright file="HoldPositionRefinerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The panel photo can misplace a hold on its facet; when the capture photos agree on where it really is,
/// their own protrusion smear accounted for, the footprint is built there (and the shift recorded).
/// </summary>
public class HoldPositionRefinerTests
{
    private static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    /// <summary>Capture cameras spread around the hold (their per-mm displacements differ by up to 0.8).</summary>
    private static readonly double[][] Cams = [[-600, -1500, 0], [600, -1500, 0], [0, -1500, -600], [0, -1500, 600], [-400, -1500, 400], [400, -1500, -400]];

    [Fact]
    public void AgreeingViews_GiveTheirShift()
    {
        var views = new[] { 7.0, 8, 9, 8, 7, 10 }.Select((a, i) => Box(a, -5.5 + (i % 2), cam: i)).ToList();

        var shift = HoldPositionRefiner.Shift(Wall, Box(0, 0), views)!.Value;

        Assert.Equal(8, shift.A, 0.6);
        Assert.Equal(-5, shift.B, 0.6);
    }

    [Fact]
    public void ScatteredOrTooFewViews_GiveNoShift()
    {
        var scattered = new[] { (12.0, 0.0), (-12.0, 3.0), (0.0, 14.0), (3.0, -13.0), (10.0, 10.0), (-9.0, -9.0) }
            .Select((d, i) => Box(d.Item1, d.Item2, cam: i)).ToList();

        Assert.Null(HoldPositionRefiner.Shift(Wall, Box(0, 0), scattered));
        Assert.Null(HoldPositionRefiner.Shift(Wall, Box(0, 0), Around(9, 0, n: 3)));
        Assert.Null(HoldPositionRefiner.Shift(Wall, Box(0, 0), Around(2, 1)));

        // all from one viewpoint: height and position cannot be told apart
        Assert.Null(HoldPositionRefiner.Shift(Wall, Box(0, 0), Enumerable.Repeat(Box(9, 0), 8).ToList()));
    }

    [Fact]
    public void ShiftsBeyondHalfTheHoldOrTheCap_AreNotTrusted()
    {
        // a 20 mm pocket "found" 14 mm away is the next pocket of a hangboard, not this one
        Assert.Null(HoldPositionRefiner.Shift(Wall, Box(0, 0, 10), Around(14, 0, 10)));
        Assert.Null(HoldPositionRefiner.Shift(Wall, Box(0, 0, 40), Around(30, 0, 40)));
        Assert.NotNull(HoldPositionRefiner.Shift(Wall, Box(0, 0, 40), Around(10, 0, 40)));
    }

    [Fact]
    public void Estimate_BuildsTheFootprintAtTheAgreedPlace_AndRecordsTheShift()
    {
        var frame = HoldFootprintEstimatorTests.Wall;
        double[] below = [0, -1500, -1500], right = [1400, -1500, 200], left = [-1400, -1500, 200];
        var primary = new FootprintView(HoldFootprintEstimatorTests.Silhouette(below), below, "panel");
        List<(double A, double B)> Moved(double[] cam) =>
            HoldFootprintEstimatorTests.Silhouette(cam).Select(p => (p.A + 10, p.B)).ToList();
        var others = new[] { below, right, left, below, right, left }.Select((c, i) => new FootprintView(Moved(c), c, $"c{i}")).ToList();

        var fp = HoldFootprintEstimator.Estimate(frame, (0, 0), primary, others, "k")!;

        Assert.Equal(HoldFootprintSource.MultiView, fp.Source);
        Assert.InRange(fp.ShiftA, 8, 12);
        Assert.Equal(0, fp.ShiftB, 1.0);
        Assert.Equal(10, fp.Outline.Average(p => p[0]), 3.0);
    }

    private static FootprintView Box(double a, double b, double half = 30, int cam = -1) =>
        new([(a - half, b - half), (a + half, b - half), (a + half, b + half), (a - half, b + half)], cam < 0 ? [0, -1500, 0] : Cams[cam % Cams.Length], "v");

    private static List<FootprintView> Around(double a, double b, double half = 30, int n = 8) =>
        Enumerable.Range(0, n).Select(i => Box(a, b, half, i)).ToList();
}
