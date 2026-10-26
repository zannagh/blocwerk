// <copyright file="HoldMoveToleranceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>How far a carried hold may appear to shift without having moved, and what that does to the verdict.</summary>
public class HoldMoveToleranceTests
{
    private static readonly HoldMoveOptions Options = new();

    private static Hold Sized(double widthMm, double heightMm, double? apexMm = null)
    {
        var hold = new Hold { X = 0.5, Y = 0.5, Radius = 0.01, WidthMm = widthMm, HeightMm = heightMm };
        if (apexMm is { } apex)
        {
            hold.ProtrusionMm = new HoldProtrusion(HoldProtrusionSource.Splat, 100, 0, apex * 0.8, 0, 0, apex, HoldFootprint.KeyOf(hold)).ToJson();
        }

        return hold;
    }

    [Fact]
    public void Photo_IsTheLargerOfThePanelNoiseAndTheSizeShare_PlusTheApex()
    {
        var old = Sized(60, 60, apexMm: 40);
        var twin = Sized(60, 50);

        // The size share is 0.45 × 60 / 2 = 13.5: a noisy panel's 2 × 8 wins, a quiet one gives way to it.
        Assert.Equal(16 + 40, HoldMoveTolerance.Photo(8, old, twin, Options), 6);
        Assert.Equal(13.5 + 40, HoldMoveTolerance.Photo(1, old, twin, Options), 6);

        // An unmeasured panel has the fixed photo floor; a big hold its size share.
        Assert.Equal(25 + 40, HoldMoveTolerance.Photo(null, old, twin, Options), 6);
        Assert.Equal(45 + 40, HoldMoveTolerance.Photo(8, old, Sized(200, 120), Options), 6);
    }

    [Fact]
    public void ThreeD_IsTheLargerOfTheNeighbourhoodsSpreadAndTheSizeShare_PlusTheApex()
    {
        var old = Sized(100, 100, apexMm: 20);

        Assert.Equal(22.5 + 20, HoldMoveTolerance.ThreeD(6, old, Sized(100, 100), Options), 6);
        Assert.Equal(35 + 20, HoldMoveTolerance.ThreeD(10, old, Sized(100, 100), Options), 6);
    }

    [Fact]
    public void Apex_IsTheMeasuredOne_NeverBelowTheWall_ElseTheSizeEstimate()
    {
        var below = Sized(100, 100, apexMm: -40);
        var unmeasured = Sized(100, 100);
        var estimate = HoldProtrusion.Estimate(100, 100, string.Empty).ApexMm;

        Assert.Equal(22.5, HoldMoveTolerance.Combine(0, below, Sized(0, 0), Options), 6);
        Assert.Equal(22.5 + estimate, HoldMoveTolerance.Combine(0, unmeasured, Sized(0, 0), Options), 6);
    }

    [Fact]
    public void PanelNoise_IsThe90thPercentile_OnlyOverEnoughPairs()
    {
        Assert.Null(HoldMoveTolerance.PanelNoiseMm(Enumerable.Range(0, HoldMoveTolerance.MinNoiseSamples - 1).Select(i => (double)i)));

        // 0..99 mm: one hold in ten is above 89 mm, whatever the few far movers do.
        Assert.Equal(89, HoldMoveTolerance.PanelNoiseMm(Enumerable.Range(0, 100).Select(i => (double)i))!.Value, 6);
    }

    [Theory]
    [InlineData(60, 50, false, HoldMoveOutcome.Kept)]
    [InlineData(60, 70, false, HoldMoveOutcome.Stayed)]
    [InlineData(60, 70, true, HoldMoveOutcome.Kept)]
    [InlineData(150, 170, false, HoldMoveOutcome.Possible)]
    public void Policy_AnUnconfirmedMoveMustLeaveTheTolerance_AConfirmedOneOnlyTheFixedFloor_AndTheCutoffAlwaysApplies(
        double mm, double toleranceMm, bool confirmed, HoldMoveOutcome expected)
    {
        var measure = new HoldMoveMeasure(mm, HoldMoveSource.TwoD, null, ToleranceMm: toleranceMm);

        Assert.Equal(expected, HoldMovePolicy.Classify(measure, Options, confirmed));
    }

    [Fact]
    public void Rotation_OfALowAspectOutline_SaysNothing_SoATurnAloneIsNoMove()
    {
        var low = new HoldFingerprint { Aspect = 1.8, OrientationDeg = 10 };
        var lowTurned = new HoldFingerprint { Aspect = 1.8, OrientationDeg = 80 };
        var long1 = new HoldFingerprint { Aspect = 2.5, OrientationDeg = 10 };
        var long2 = new HoldFingerprint { Aspect = 2.5, OrientationDeg = 80 };

        Assert.Null(HoldMoveCalculator.Rotation(low, lowTurned));
        Assert.Null(HoldMoveCalculator.Rotation(low, long2));
        Assert.Equal(70, HoldMoveCalculator.Rotation(long1, long2)!.Value, 6);
    }

    [Fact]
    public void Reconcile_ThePhotoVetoesA3DMoveWithinItsOwnTolerance()
    {
        var three = new HoldMoveMeasure(56, HoldMoveSource.ThreeD, null, 4, true);
        var photo = new HoldMoveMeasure(57, HoldMoveSource.TwoD, null, Corroborated: true, ToleranceMm: 118);

        var measure = WallBigUpdateService.Reconcile(three, photo, Options)!;

        Assert.Equal(HoldMoveSource.TwoD, measure.Source);
        Assert.Equal(HoldMoveOutcome.Stayed, HoldMovePolicy.Classify(measure, Options));
    }
}
