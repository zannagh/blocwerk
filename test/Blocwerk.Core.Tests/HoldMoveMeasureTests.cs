// <copyright file="HoldMoveMeasureTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>The displacement of a carried hold (3D, 2D fallback), the cutoff rules and the panel scale.</summary>
public class HoldMoveMeasureTests
{
    private static readonly HoldMoveOptions Options = new();

    private static Hold Placed(string facet, double a, double b, double x = 0.5, double y = 0.5) =>
        new() { FacetId = facet, PlaneAMm = a, PlaneBMm = b, X = x, Y = y };

    [Fact]
    public void ThreeD_SameFacet_IsThePlaneDistance()
    {
        var measure = HoldMoveCalculator.Measure(Placed("0", 1000, 1000), Placed("0", 1030, 1040), null, null, null);

        Assert.NotNull(measure);
        Assert.Equal(HoldMoveSource.ThreeD, measure!.Source);
        Assert.Equal(50, measure.DistanceMm, 3);
    }

    [Fact]
    public void ThreeD_DifferentFacets_FallsBackToTwoD()
    {
        var old = Placed("0", 1000, 1000);
        var twin = Placed("1", 20, 20, x: 0.60, y: 0.5);

        var measure = HoldMoveCalculator.Measure(old, twin, new HoldPositionNorm(0.5, 0.5), (1000, 1000), 2.0);

        Assert.Equal(HoldMoveSource.TwoD, measure!.Source);
        Assert.Equal(200, measure.DistanceMm, 3);
    }

    [Fact]
    public void TwoD_NeedsAWarpSpotAndAScale()
    {
        var old = new Hold { X = 0.5, Y = 0.5 };
        var twin = new Hold { X = 0.6, Y = 0.5 };

        Assert.Null(HoldMoveCalculator.Measure(old, twin, null, (1000, 1000), 2.0));
        Assert.Null(HoldMoveCalculator.Measure(old, twin, new HoldPositionNorm(0.5, 0.5), (1000, 1000), null));
        Assert.Equal(300, HoldMoveCalculator.Measure(old, twin, new HoldPositionNorm(0.5, 0.5), (3000, 1000), 1.0)!.DistanceMm, 3);
    }

    [Fact]
    public void ThreeD_RejectedPlacementIsNotUsed()
    {
        var twin = Placed("0", 1000, 1000);
        twin.MetricSource = HoldMetric.TextureRegistrationRejected;

        Assert.Null(HoldMoveCalculator.Distance3D(Placed("0", 1000, 1000), twin));
    }

    [Fact]
    public void Rotation_NeedsElongatedHoldsAndWrapsAt180()
    {
        var a = new HoldFingerprint { Aspect = 2, OrientationDeg = 170 };
        var b = new HoldFingerprint { Aspect = 2, OrientationDeg = 20 };

        Assert.Equal(30, HoldMoveCalculator.Rotation(a, b)!.Value, 3);
        Assert.Null(HoldMoveCalculator.Rotation(new HoldFingerprint { Aspect = 1.1 }, b));
        Assert.Null(HoldMoveCalculator.Rotation(null, b));
    }

    [Theory]
    [InlineData(10, HoldMoveSource.ThreeD, HoldMoveOutcome.Stayed)]
    [InlineData(30, HoldMoveSource.ThreeD, HoldMoveOutcome.Kept)]
    [InlineData(100, HoldMoveSource.ThreeD, HoldMoveOutcome.Kept)]
    [InlineData(100.1, HoldMoveSource.ThreeD, HoldMoveOutcome.Possible)]
    [InlineData(20, HoldMoveSource.TwoD, HoldMoveOutcome.Stayed)]
    [InlineData(30, HoldMoveSource.TwoD, HoldMoveOutcome.Kept)]
    [InlineData(340, HoldMoveSource.TwoD, HoldMoveOutcome.Possible)]
    public void Policy_AppliesTheCutoffAndTheNoiseFloor(double mm, HoldMoveSource source, HoldMoveOutcome expected)
    {
        Assert.Equal(expected, HoldMovePolicy.Classify(new HoldMoveMeasure(mm, source, null), Options));
    }

    [Fact]
    public void Policy_TheCutoffIsConfigurable()
    {
        var strict = new HoldMoveOptions { CutoffMm = 50 };

        Assert.Equal(HoldMoveOutcome.Possible, HoldMovePolicy.Classify(new HoldMoveMeasure(60, HoldMoveSource.ThreeD, null), strict));
    }

    [Theory]
    [InlineData(340, true, true, HoldMoveOutcome.Removed)]
    [InlineData(340, false, true, HoldMoveOutcome.Possible)]
    [InlineData(340, true, false, HoldMoveOutcome.Possible)]
    [InlineData(120, true, true, HoldMoveOutcome.Possible)]
    [InlineData(150, true, true, HoldMoveOutcome.Removed)]
    public void Policy_OnlyAConfirmedConfidentMoveWellPastTheCutoffTakesTheHoldOff(double mm, bool confirmed, bool confident, HoldMoveOutcome expected)
    {
        Assert.Equal(expected, HoldMovePolicy.Classify(new HoldMoveMeasure(mm, HoldMoveSource.ThreeD, null, 5, confident), Options, confirmed));
    }

    [Fact]
    public void Policy_TheFloorGrowsWithTheNeighbourhoodsSpread_AndAnUnsureSmallMoveIsNotFlagged()
    {
        Assert.Equal(HoldMoveOutcome.Stayed, HoldMovePolicy.Classify(new HoldMoveMeasure(60, HoldMoveSource.ThreeD, null, 20), Options));
        Assert.Equal(HoldMoveOutcome.Kept, HoldMovePolicy.Classify(new HoldMoveMeasure(60, HoldMoveSource.ThreeD, null, 5), Options));
        Assert.Equal(HoldMoveOutcome.Stayed, HoldMovePolicy.Classify(new HoldMoveMeasure(60, HoldMoveSource.ThreeD, null, 40, false), Options));
    }

    [Theory]
    [InlineData(true, true, true, HoldMoveOutcome.Removed)]
    [InlineData(false, true, true, HoldMoveOutcome.Possible)]
    [InlineData(true, false, true, HoldMoveOutcome.Possible)]
    [InlineData(true, true, false, HoldMoveOutcome.Possible)]
    public void Policy_RemoveUnconfirmedBeyondCutoff_NeedsTheOptionAConfidentMeasureAndThePhotoBehindIt(
        bool option, bool corroborated, bool confident, HoldMoveOutcome expected)
    {
        var options = new HoldMoveOptions { RemoveUnconfirmedBeyondCutoff = option };
        var measure = new HoldMoveMeasure(300, HoldMoveSource.TwoD, null, null, confident, Corroborated: corroborated);

        Assert.Equal(expected, HoldMovePolicy.Classify(measure, options, confirmedMove: false));
    }

    [Fact]
    public void Policy_WithTheOptionOff_AConfidentPhotoMoveBeyondTheCutoffIsOnlyPossible_AndOnTheOptionRemoves()
    {
        var measure = new HoldMoveMeasure(340, HoldMoveSource.TwoD, null, null, true, Corroborated: true);

        Assert.Equal(HoldMoveOutcome.Possible, HoldMovePolicy.Classify(measure, new HoldMoveOptions()));
        Assert.Equal(HoldMoveOutcome.Removed, HoldMovePolicy.Classify(measure, new HoldMoveOptions { RemoveUnconfirmedBeyondCutoff = true }));
    }

    [Fact]
    public void Reconcile_MarksPhotoBackedMeasures()
    {
        var three = new HoldMoveMeasure(90, HoldMoveSource.ThreeD, null, 6, true);

        Assert.True(WallBigUpdateService.Reconcile(three, new HoldMoveMeasure(85, HoldMoveSource.TwoD, null), Options)!.Corroborated);
        Assert.False(WallBigUpdateService.Reconcile(three, null, Options)!.Corroborated);
    }

    [Fact]
    public void Reconcile_ThePhotoEstimateOverrulesAStale3DMove_AndAgreeingNumbersUse3D()
    {
        var three = new HoldMoveMeasure(90, HoldMoveSource.ThreeD, null, 6, true);

        Assert.Equal(HoldMoveSource.TwoD, WallBigUpdateService.Reconcile(three, new HoldMoveMeasure(5, HoldMoveSource.TwoD, null), Options)!.Source);
        Assert.Equal(three with { Corroborated = true }, WallBigUpdateService.Reconcile(three, new HoldMoveMeasure(85, HoldMoveSource.TwoD, null), Options));
        Assert.Equal(200, WallBigUpdateService.Reconcile(three, new HoldMoveMeasure(200, HoldMoveSource.TwoD, null), Options)!.DistanceMm);
        Assert.Same(three, WallBigUpdateService.Reconcile(three, null, Options));
        var onlyPhoto = new HoldMoveMeasure(70, HoldMoveSource.TwoD, null);
        Assert.Same(onlyPhoto, WallBigUpdateService.Reconcile(null, onlyPhoto, Options));
        var unsure = WallBigUpdateService.Reconcile(three with { Confident = false }, onlyPhoto, Options)!;
        Assert.Equal((HoldMoveSource.TwoD, 70), (unsure.Source, unsure.DistanceMm));
    }

    [Fact]
    public void Policy_AHoldTurnedWithoutMovingStillCountsAsMoved()
    {
        Assert.Equal(HoldMoveOutcome.Kept, HoldMovePolicy.Classify(new HoldMoveMeasure(5, HoldMoveSource.ThreeD, 60), Options));
        Assert.Equal(HoldMoveOutcome.Stayed, HoldMovePolicy.Classify(new HoldMoveMeasure(5, HoldMoveSource.ThreeD, 20), Options));
    }

    [Fact]
    public void Text_IsPlainLanguage()
    {
        Assert.Equal("moved 6 cm, kept", HoldMovePolicy.Describe(60, null, HoldMoveOutcome.Kept));
        Assert.Equal("moved 34 cm, removed from this boulder", HoldMovePolicy.Describe(340, null, HoldMoveOutcome.Removed));
        Assert.Equal("turned 60°, kept", HoldMovePolicy.Describe(5, 60, HoldMoveOutcome.Kept));
        Assert.Equal("hold moved 6 cm", HoldMovePolicy.Reason(60, HoldMoveOutcome.Kept));
        Assert.Equal("hold moved 34 cm, removed", HoldMovePolicy.Reason(340, HoldMoveOutcome.Removed));
    }

    [Fact]
    public void Scale_IsTheMedianOverPairs_SoOneMoverDoesNotSkewIt()
    {
        // Holds on a grid, 100 mm per 0.05 of a 1000 px photo = 2 mm/px; the last one "moved" far in the new photo.
        var pairs = Enumerable.Range(0, 6).Select(i =>
        {
            var old = Placed("0", i * 200, 0);
            old.Id = new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
            var twin = new Hold { X = (i * 100.0 / 1000) + 0.1, Y = 0.5 };
            return (old, twin);
        }).ToList();
        pairs[5].twin.X = 0.95;

        var scale = PanelScaleEstimator.Estimate(pairs, (1000, 1000));

        Assert.Equal(2.0, scale!.Value, 1);
        Assert.Null(PanelScaleEstimator.Estimate(pairs.Take(2), (1000, 1000)));
    }
}
