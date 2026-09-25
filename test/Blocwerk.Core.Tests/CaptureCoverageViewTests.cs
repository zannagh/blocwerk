// <copyright file="CaptureCoverageViewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using static Blocwerk.Core.Tests.CoverageFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The view coverage of the capture coverage report on synthetic geometry with known camera layouts: a cell seen from
/// one direction vs four, only grazing views, only far views, no view, a line of sight blocked by a volume, and a
/// volume whose underside nobody shot.
/// </summary>
public class CaptureCoverageViewTests
{
    private static readonly double[] Middle = At(1500, 1500);

    [Fact]
    public void ACellSeenFromOneDirection_IsWeak_AndFromFourDirections_IsGood()
    {
        var one = CaptureCoverageAnalyzer.Analyze(Inputs(Document(), [Photo([1500, -2500, 1500], Middle)]), DateTimeOffset.UnixEpoch);
        var four = CaptureCoverageAnalyzer.Analyze(
            Inputs(Document(), [.. new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) }.Select(d => Photo([1500 + (d.Item1 * 1200), -2500, 1500 + (d.Item2 * 1200)], Middle))]),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(CoverageCellStatus.FewDirections, CellAt(one, 1500, 1500));
        Assert.Equal(CoverageCellStatus.Good, CellAt(four, 1500, 1500));
        var views = PointViews.Evaluate(Middle, [0, -1, 0], "0", [.. new[] { -1, 1 }.Select(d => Photo([1500 + (d * 1200), -2500, 1500], Middle))], Scene());
        Assert.Equal((2, 2), (views.Views, views.Directions));
        Assert.InRange(views.SpreadDeg, 50, 60);
        Assert.Contains(one.Advice, a => a.Kind == "surface" && a.Text.Contains("from more directions: it is seen from only 1", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyGrazingViews_AreFlagged_EvenFromSeveralDirections()
    {
        CoverageCamera[] grazing =
        [
            Photo([-2500, -400, 800], Middle), Photo([-2500, -400, 2200], Middle), Photo([5500, -400, 1500], Middle),
        ];

        var views = PointViews.Evaluate(Middle, [0, -1, 0], "0", grazing, Scene());

        Assert.Equal(3, views.Directions);
        Assert.True(views.BestAngleDeg > PointViews.GrazingDeg);
        Assert.Equal(CoverageCellStatus.Grazing, views.Status);
    }

    [Fact]
    public void OnlyFarViews_AreLowResolution_AndNoView_IsNever()
    {
        CoverageCamera[] far = [.. new[] { -1, 0, 1 }.Select(d => Photo([1500 + (d * 4000), -8000, 1500], Middle))];
        CoverageCamera[] away = [Photo([1500, -2500, 1500], [1500, -5000, 1500])];

        var farViews = PointViews.Evaluate(Middle, [0, -1, 0], "0", far, Scene());

        Assert.True(farViews.BestMmPerPx > PointViews.MaxMmPerPx);
        Assert.Equal(CoverageCellStatus.LowResolution, farViews.Status);
        Assert.Equal(CoverageCellStatus.Never, PointViews.Evaluate(Middle, [0, -1, 0], "0", away, Scene()).Status);
        Assert.Equal(CoverageCellStatus.Never, PointViews.Evaluate(Middle, [0, -1, 0], "0", [Photo([1500, 2500, 1500], Middle)], Scene()).Status);
    }

    [Fact]
    public void AVolumeInTheLineOfSight_OccludesTheCell_AndTheWallUnderItIsNotRated()
    {
        var target = At(1500, 600);
        var camera = Photo([1500, -1000, 2600], target);
        var volume = Block(1, 1500, 1000, sizeMm: 400, heightMm: 300);

        Assert.Equal(1, PointViews.Evaluate(target, [0, -1, 0], "0", [camera], Scene()).Views);
        Assert.Equal(0, PointViews.Evaluate(target, [0, -1, 0], "0", [camera], Scene(volume)).Views);
        Assert.True(Scene(volume).UnderVolume("0", 1500, 1000));

        var report = CaptureCoverageAnalyzer.Analyze(Inputs(Document(), [camera], [volume]), DateTimeOffset.UnixEpoch);
        Assert.Equal(CoverageCellStatus.Hidden, CellAt(report, 1500, 1000));
    }

    [Fact]
    public void AVolumeShotOnlyFromAbove_HasAWeakUnderside_AndTheListSaysShootItFromBelow()
    {
        var volume = Block(1, 1500, 1800, sizeMm: 500, heightMm: 250, slopeMm: 120);
        var top = At(1500, 1800);
        CoverageCamera[] above = [.. new[] { 500.0, 1500, 2500 }.Select((x, i) => Photo([x, -1200, 3600], top, $"p{i}"))];

        var report = CaptureCoverageAnalyzer.Analyze(Inputs(Document(), above, [volume]), DateTimeOffset.UnixEpoch);

        var faces = report.Volumes.Single().Faces.ToDictionary(f => f.Face);
        Assert.NotEqual(CoverageCellStatus.Good, faces[VolumeFace.Underside].Status);
        Assert.Equal(CoverageCellStatus.Good, faces[VolumeFace.TopSide].Status);
        Assert.Equal("Shoot the underside of volume 1 (on the wall) from below", report.Advice[0].Text);
        Assert.Equal(VolumeFace.Underside, VolumeCoverageRater.FaceOf([0, -0.9, 0.3]));
        Assert.Equal(VolumeFace.Front, VolumeCoverageRater.FaceOf([0.1, 0, 0.95]));
        Assert.Equal(VolumeFace.RightSide, VolumeCoverageRater.FaceOf([0.9, 0.2, 0.3]));
    }

    [Fact]
    public void TheReport_RoundTripsThroughItsJson()
    {
        var report = CaptureCoverageAnalyzer.Analyze(
            Inputs(Document((1, 300, 300, 5)), [Photo([1500, -2500, 1500], Middle)], [Block(1, 1500, 1800)]), DateTimeOffset.UnixEpoch);

        var json = report.ToJson();
        var back = CaptureCoverageReport.Parse(json);

        Assert.NotNull(back);
        Assert.Equal(report.Facets[0].Cells, back.Facets[0].Cells);
        Assert.Equal(report.Advice.Select(a => a.Text), back.Advice.Select(a => a.Text));
        Assert.Contains("\"posesFrom\":\"photos\"", json, StringComparison.Ordinal);
        Assert.Null(CaptureCoverageReport.Parse("{not json"));
    }
}
