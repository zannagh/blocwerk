// <copyright file="AtticMoveRegressionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Attic's gen 6 → 7 update, replayed through the move plan (<see cref="AtticMoveFixture"/>). In the run, 31 carried holds
/// were "moved" and none had moved: 5 of them flagged 9 boulders (two-thirds on holds standing 85–157 mm out of the wall), 14
/// "turned" low-aspect outlines. The one real move (the orange hold, ~1100 px) was not a carried pair.
/// </summary>
public class AtticMoveRegressionTests
{
    private static readonly string[] Triggers = ["89ad8b6b", "0cc8f5e5", "4fdd1df1", "bbcf909e", "c658f70c"];

    private static readonly Lazy<AtticMoveFixture> Fixture = new(AtticMoveFixture.Load);

    [Fact]
    public void TheFixtureReproducesTheRunsMeasures()
    {
        var moves = Plan(Fixture.Value.Pairs);

        foreach (var key in Triggers)
        {
            var run = Fixture.Value.MovedInRun[key];
            var measure = Single(moves, key).Measure;
            var replayed = run.Source == 0 ? measure.ThreeDMm!.Value : measure.DistanceMm;
            Assert.True(Math.Abs(replayed - run.Mm) < 2, $"{key}: run {run.Mm} mm, replayed {replayed:F1} mm");
        }
    }

    [Fact]
    public void NoneOfTheFiveHoldsBehindTheFlaggedBoulders_IsSuspected_EachWithinItsOwnTolerance()
    {
        var moves = Plan(Fixture.Value.Pairs);

        foreach (var key in Triggers)
        {
            var move = Single(moves, key);
            Assert.Equal(HoldMoveOutcome.Stayed, move.Outcome);
            Assert.True(move.Measure.DistanceMm < move.Measure.ToleranceMm, $"{key}: {move.Measure.DistanceMm:F1} mm, tolerance {move.Measure.ToleranceMm:F1}");
        }
    }

    [Fact]
    public void NoHoldTurnedOnlyByItsOutlinesAxis_AndTheOnlySuspicionLeftIsTheFarOne()
    {
        var moves = Plan(Fixture.Value.Pairs);

        var turned = Fixture.Value.MovedInRun.Where(m => m.Value.Text.Contains("turned", StringComparison.Ordinal)).Select(m => m.Key).ToList();
        Assert.Equal(14, turned.Count);
        Assert.All(turned, key => Assert.Equal(HoldMoveOutcome.Stayed, Single(moves, key).Outcome));

        // "Possibly 14 cm" (49d97a55, no boulders): beyond the 10 cm cutoff no tolerance applies; it stays a review item.
        var suspected = moves.Where(m => m.Outcome == HoldMoveOutcome.Suspected).Select(m => m.OldHoldId.ToString()[..8]).ToList();
        Assert.Equal(["49d97a55"], suspected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARealFiveCmMoveOfAFlatHold_IsStillSuspected(bool placedIn3D)
    {
        var pairs = Fixture.Value.Pairs.ToList();
        var index = pairs.FindIndex(p => IsFlatTypicalHold(p.Old, p.Twin));
        var (old, twin) = pairs[index];
        var size = Fixture.Value.Sizes[twin.WallPanelId!.Value];
        var mmPerPx = PanelScaleEstimator.Estimate(pairs.Where(p => p.Twin.WallPanelId == twin.WallPanelId), size)!.Value;
        var moved = twin.Clone();
        (moved.Id, moved.X) = (twin.Id, twin.X + (50 / mmPerPx / size.Width));
        (moved.FacetId, moved.PlaneAMm, moved.PlaneBMm) = placedIn3D ? (twin.FacetId, twin.PlaneAMm + 50, twin.PlaneBMm) : (null, null, null);
        pairs[index] = (old, moved);

        var move = Single(Plan(pairs), old.Id.ToString()[..8]);

        Assert.Equal(HoldMoveOutcome.Suspected, move.Outcome);
        Assert.Equal(placedIn3D ? HoldMoveSource.ThreeD : HoldMoveSource.TwoD, move.Measure.Source);
    }

    [Theory]
    [InlineData(false, HoldMoveOutcome.Suspected)]
    [InlineData(true, HoldMoveOutcome.Removed)]
    public void TheOrangeHoldsRealFarMove_AsAPair_IsStillSuspected_AndConfirmedTakesItOff(bool confirmed, HoldMoveOutcome expected)
    {
        // Gen 6 c57a2626 sat where the new black hold now is (its staged spot is the warp's); it was found again ~1100 px left.
        var centre = AtticMoveFixture.PanelId("6ad14000");
        var old = new Hold { Id = Guid.Parse("c57a2626-43f9-4e12-ab4f-c390b6a46bda"), X = 0.37687, Y = 0.27075, Radius = 0.0193, WallPanelId = centre };
        var twin = new Hold { Id = Guid.NewGuid(), X = 0.1037, Y = 0.2601, Radius = 0.0193, WallPanelId = centre };
        var pairs = Fixture.Value.Pairs.Append((old, twin)).ToList();
        var warp = new Dictionary<Guid, HoldPositionNorm>(Fixture.Value.Warp) { [old.Id] = new HoldPositionNorm(0.3804, 0.2776) };

        var move = WallBigUpdateService.PlanPairs(
                pairs, new BigUpdateConfirmation([], [], [], [], warp), Fixture.Value.Sizes, confirmed ? new HashSet<Guid> { old.Id } : [], new HoldMoveOptions())
            .Single(m => m.OldHoldId == old.Id);

        Assert.Equal(expected, move.Outcome);
        Assert.InRange(move.Measure.DistanceMm, 1500, 2500);
    }

    private static List<PlannedMove> Plan(IReadOnlyList<(Hold Old, Hold Twin)> pairs) =>
        WallBigUpdateService.PlanPairs(pairs, new BigUpdateConfirmation([], [], [], [], Fixture.Value.Warp), Fixture.Value.Sizes, new HashSet<Guid>(), new HoldMoveOptions());

    private static PlannedMove Single(List<PlannedMove> moves, string key) =>
        moves.Single(m => m.OldHoldId.ToString().StartsWith(key, StringComparison.Ordinal));

    /// <summary>A centre-panel hold of about the median size, standing at most 15 mm out of the wall, placed in 3D, that stayed.</summary>
    private static bool IsFlatTypicalHold(Hold old, Hold twin) =>
        twin.WallPanelId == AtticMoveFixture.PanelId("6ad14000") && twin.FacetId == old.FacetId && twin.PlaneAMm is not null
        && Geometry.View3D.HoldProtrusion.For(old) is { Source: Geometry.View3D.HoldProtrusionSource.Splat, ApexMm: > 0 and <= 15 }
        && old.WidthMm is >= 90 and <= 130 && Math.Abs(twin.X - Fixture.Value.Warp[old.Id].X) < 0.001;
}
