// <copyright file="HoldLookCheckTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>The look check's rules on their own: the bands, what cannot be measured, and which holds mark their boulders.</summary>
public class HoldLookCheckTests
{
    [Theory]
    [InlineData(0.9, HoldLook.Same)]
    [InlineData(0.5, HoldLook.Same)]
    [InlineData(0.49, HoldLook.Unsure)]
    [InlineData(0.35, HoldLook.Unsure)]
    [InlineData(0.34, HoldLook.Replaced)]
    [InlineData(-0.2, HoldLook.Replaced)]
    [InlineData(null, HoldLook.Unsure)]
    public void Judge_UsesTheDefaultBands_AndNeverCallsAnUnmeasuredPairReplaced(double? score, HoldLook expected) =>
        Assert.Equal(expected, HoldLookCheck.Judge(score, new HoldMoveOptions()));

    [Fact]
    public void Judge_FollowsTheConfiguredThresholds()
    {
        var options = new HoldMoveOptions { SameLookMinScore = 0.8, ReplacedBelowScore = 0.6, StillThereMinScore = 0.9 };

        Assert.Equal(HoldLook.Unsure, HoldLookCheck.Judge(0.7, options));
        Assert.Equal(HoldLook.Replaced, HoldLookCheck.Judge(0.55, options));
        Assert.False(HoldLookCheck.StillThere(0.85, options));
        Assert.True(HoldLookCheck.StillThere(0.6, new HoldMoveOptions()));
        Assert.False(HoldLookCheck.StillThere(0.59, new HoldMoveOptions()));
    }

    [Fact]
    public void Query_OutsideTheNewPhoto_OrWithoutAKnownScale_IsNotMeasurable()
    {
        PointPair[] pairs = [new(100, 100, 110, 110), new(900, 100, 990, 110), new(100, 700, 110, 770), new(900, 700, 990, 770)];

        var inside = HoldLookCheck.Query(pairs, (500, 400), (550, 440), (1000, 800));
        Assert.Equal(1.1, inside!.Value.Scale, 3);
        Assert.Null(HoldLookCheck.Query(pairs, (-5, 400), (0, 440), (1000, 800)));
        Assert.Null(HoldLookCheck.Query(pairs, (500, 800), (550, 880), (1000, 800)));
        Assert.Null(HoldLookCheck.Query(pairs[..2], (500, 400), (550, 440), (1000, 800)));
    }

    [Fact]
    public void NotFoundToMark_LeavesOutWhatAPersonSettled()
    {
        var (missing, confirmed, replacedKept, removed, paired, undecided) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var notFound = new Dictionary<Guid, HoldMoveOutcome>
        {
            [missing] = HoldMoveOutcome.NotFound, [confirmed] = HoldMoveOutcome.NotFound, [replacedKept] = HoldMoveOutcome.Replaced,
            [removed] = HoldMoveOutcome.Replaced, [paired] = HoldMoveOutcome.NotFound, [undecided] = HoldMoveOutcome.NotFound,
        };
        var confirmation = new BigUpdateConfirmation(
            [
                new CarryoverDecision(missing, CarryKind.Carried, null),
                new CarryoverDecision(confirmed, CarryKind.Carried, null, Confirmed: true),
                new CarryoverDecision(replacedKept, CarryKind.Carried, null, Confirmed: true),
                new CarryoverDecision(removed, CarryKind.Removed, null, Confirmed: true),
                new CarryoverDecision(paired, CarryKind.Carried, Guid.NewGuid()),
            ],
            [],
            [],
            [],
            NotFoundAgain: notFound);

        var marked = WallBigUpdateService.NotFoundToMark(confirmation);

        Assert.Equal(new[] { missing, replacedKept, undecided }.Order(), marked.Keys.Order());
        Assert.Equal(HoldMoveOutcome.Replaced, marked[replacedKept]);
        Assert.Empty(WallBigUpdateService.NotFoundToMark(confirmation with { NotFoundAgain = null }));
    }

    [Fact]
    public void ThePlanVersion_CoversTheNotFoundHolds_AndIsUnchangedWithoutThem()
    {
        var hold = Guid.NewGuid();
        var none = new HoldMovePlan([]);

        Assert.Equal(HoldMovePlan.Empty.Version, none.Version);
        Assert.Equal(none.Version, new HoldMovePlan([], new Dictionary<Guid, HoldMoveOutcome>()).Version);
        var notFound = new HoldMovePlan([], new Dictionary<Guid, HoldMoveOutcome> { [hold] = HoldMoveOutcome.NotFound });
        var replaced = new HoldMovePlan([], new Dictionary<Guid, HoldMoveOutcome> { [hold] = HoldMoveOutcome.Replaced });
        Assert.NotEqual(none.Version, notFound.Version);
        Assert.NotEqual(notFound.Version, replaced.Version);
        Assert.Equal(HoldMoveOutcome.Replaced, replaced.NotFoundFor(hold, Guid.NewGuid())!.Outcome);
        Assert.Null(replaced.NotFoundFor(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void TheTexts_SayWhyTheBoulderNeedsALook()
    {
        Assert.Equal("not found again on the new photo, check this hold", HoldMovePolicy.Describe(0, null, HoldMoveOutcome.NotFound));
        Assert.Equal("another hold is here now, check this hold", HoldMovePolicy.Describe(0, null, HoldMoveOutcome.Replaced));
        Assert.Equal("hold not found again on the new photo", HoldMovePolicy.Reason(0, HoldMoveOutcome.NotFound));
        Assert.Null(HoldMovePolicy.NotFoundText(HoldMoveOutcome.Kept));
    }

    [Fact]
    public void TheMatchLine_IsMadeFromTheSummary()
    {
        var plain = new RefreshSummary(545, 345, 0, 0, 0, 0, 0, 0, 0, []);

        Assert.Equal("545 old holds found again, 345 kept where they were", RefreshTimeline.MatchDetail(plain));
        Assert.Equal(
            "457 old holds found again, 433 kept where they were, 88 replaced by another hold; 17 boulders will be marked for review",
            RefreshTimeline.MatchDetail(plain with { Refound = 457, KeptInPlace = 433, ReplacedHolds = 88, BouldersMarkedNotFound = 17 }));
        Assert.EndsWith("; 1 boulder will be marked for review", RefreshTimeline.MatchDetail(plain with { BouldersMarkedNotFound = 1 }));
    }
}
