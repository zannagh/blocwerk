// <copyright file="LookOverFindingBuilderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The findings of a promote, kind by kind, from what the promote did: the cards that need a 3D model or a hand-placed
/// hold to come up at all are built here from their facts, and so is the "new hold on the spot of a kept old hold".
/// </summary>
public class LookOverFindingBuilderTests
{
    private static readonly Guid Panel = Guid.NewGuid();

    [Fact]
    public void ADetectionThatWasLeftOut_IsAFindingWithoutAHold_AtTheSpotItWasSeen()
    {
        var detection = Hold(0.3, 0.4);
        var card = new WallUpdateException
        {
            Kind = UpdateExceptionKind.ConflictingNew, StagedHoldId = detection.Id, GeometryModelId = Guid.NewGuid(), FacetId = "f1", A = 10, B = 20,
        };

        var finding = Assert.Single(LookOverFindingBuilder.Build(Facts([card], staged: [detection])));

        Assert.Equal(
            (LookOverFindingKind.ConflictingNew, null, null, Panel, 0.3, 0.4, "f1"),
            (finding.Kind, finding.HoldId, finding.OldHoldId, finding.PanelId, finding.X, finding.Y, finding.FacetId));

        // Somebody kept the detection after all: it is a normal new hold, not a finding.
        Assert.Empty(LookOverFindingBuilder.Build(Facts([card], staged: [detection], live: [detection])));
    }

    [Fact]
    public void APossiblyRemovedHold_PointsAtItsCopy_AndAtTheSpotTheCardShowed()
    {
        var (old, copy) = (Hold(0.5, 0.5), Hold(0.52, 0.5));
        var card = new WallUpdateException { Kind = UpdateExceptionKind.PossiblyRemoved, OldHoldId = old.Id, PanelId = Panel, X = 0.51, Y = 0.49, TextureScore = 0.1 };

        var finding = Assert.Single(LookOverFindingBuilder.Build(Facts([card], live: [copy], successors: [(old, copy)])));

        Assert.Equal(
            (LookOverFindingKind.PossiblyRemoved, old.Id, copy.Id, 0.51, 0.49, (double?)0.1),
            (finding.Kind, finding.OldHoldId, finding.HoldId, finding.X, finding.Y, finding.TextureScore));
    }

    [Theory]
    [InlineData(UpdateExceptionKind.MatchedToHandPlaced, LookOverFindingKind.MatchedToHandPlaced, true)]
    [InlineData(UpdateExceptionKind.HandPlacedAmbiguous, LookOverFindingKind.HandPlacedAmbiguous, false)]
    public void TheHandPlacedCards_KeepTheirKind(UpdateExceptionKind cardKind, LookOverFindingKind kind, bool merged)
    {
        var (old, detection, copy) = (Hold(0.5, 0.5), Hold(0.5, 0.5), Hold(0.5, 0.5));
        var live = merged ? detection : copy;
        var card = new WallUpdateException { Kind = cardKind, OldHoldId = old.Id, StagedHoldId = detection.Id };
        var carry = new CarryoverDecision(old.Id, CarryKind.Carried, merged ? detection.Id : null);

        var facts = Facts([card], staged: [detection], live: [live, detection], successors: [(old, live)], carry: [carry]);
        var finding = Assert.Single(LookOverFindingBuilder.Build(facts));

        Assert.Equal((kind, live.Id, merged ? null : (Guid?)detection.Id), (finding.Kind, finding.HoldId, finding.OtherHoldId));
    }

    [Fact]
    public void ACardWhoseHoldWasRePairedOrConfirmedInTheFullReview_IsNoFinding()
    {
        var (old, detection, other) = (Hold(0.5, 0.5), Hold(0.5, 0.5), Hold(0.7, 0.5));
        var card = new WallUpdateException { Kind = UpdateExceptionKind.LowConfidenceMatch, OldHoldId = old.Id, StagedHoldId = detection.Id };
        LookOverPromoteFacts With(CarryoverDecision decision) =>
            Facts([card], staged: [detection, other], live: [detection, other], successors: [(old, detection)], carry: [decision]);

        Assert.Single(LookOverFindingBuilder.Build(With(new CarryoverDecision(old.Id, CarryKind.Carried, detection.Id))));
        Assert.Empty(LookOverFindingBuilder.Build(With(new CarryoverDecision(old.Id, CarryKind.Carried, detection.Id, Confirmed: true))));
        Assert.Empty(LookOverFindingBuilder.Build(With(new CarryoverDecision(old.Id, CarryKind.Carried, other.Id))));
        Assert.Empty(LookOverFindingBuilder.Build(With(new CarryoverDecision(old.Id, CarryKind.Removed, null))));
    }

    [Fact]
    public void AHoldNotFoundAgain_IsOnlyAFinding_WhenABoulderUsesIt()
    {
        var (old, copy) = (Hold(0.5, 0.5), Hold(0.5, 0.5));
        var notFound = new Dictionary<Guid, HoldMoveOutcome> { [old.Id] = HoldMoveOutcome.NotFound };

        Assert.Empty(LookOverFindingBuilder.Build(Facts([], live: [copy], successors: [(old, copy)], notFound: notFound)));

        var withBoulder = Facts([], live: [copy], successors: [(old, copy)], notFound: notFound, boulders: new() { [copy.Id] = 2 });
        var finding = Assert.Single(LookOverFindingBuilder.Build(withBoulder));
        Assert.Equal((LookOverFindingKind.NotFound, copy.Id, 2), (finding.Kind, finding.HoldId, finding.BouldersAtApply));
    }

    [Fact]
    public void ANewHoldOnTheSpotOfAKeptOldHold_IsAFinding_OrCompletesTheOneAboutThatHold()
    {
        var (old, copy, fresh, far) = (Hold(0.5, 0.5), Hold(0.5, 0.5), Hold(0.51, 0.5), Hold(0.9, 0.9));
        var plain = Facts([], live: [copy, fresh, far], successors: [(old, copy)]);

        var sameSpot = Assert.Single(LookOverFindingBuilder.Build(plain));
        Assert.Equal((LookOverFindingKind.SameSpot, copy.Id, fresh.Id, old.Id), (sameSpot.Kind, sameSpot.HoldId, sameSpot.OtherHoldId, sameSpot.OldHoldId));

        // Shown at the old hold's spot and at the larger hold's scale, so "before" and "now" can be compared.
        fresh.Radius = 0.05;
        var scaled = Assert.Single(LookOverFindingBuilder.Build(plain));
        Assert.Equal((copy.X, copy.Y, 0.05), (scaled.X, scaled.Y, scaled.Radius));

        // The same spot under a hold that already has a finding: that finding learns the new hold, no second one.
        var notFound = plain with { NotFound = new Dictionary<Guid, HoldMoveOutcome> { [old.Id] = HoldMoveOutcome.NotFound }, Boulders = new Dictionary<Guid, int> { [copy.Id] = 1 } };
        var known = Assert.Single(LookOverFindingBuilder.Build(notFound));
        Assert.Equal((LookOverFindingKind.NotFound, fresh.Id), (known.Kind, known.OtherHoldId));

        // A person confirmed the old hold where it is: they saw both.
        var confirmed = plain with { Carry = new Dictionary<Guid, CarryoverDecision> { [old.Id] = new(old.Id, CarryKind.Carried, null, Confirmed: true) } };
        Assert.Empty(LookOverFindingBuilder.Build(confirmed));
    }

    [Fact]
    public void AHoldCarriedOntoADetection_IsNeverASameSpotFinding()
    {
        var (old, detection, fresh) = (Hold(0.5, 0.5), Hold(0.5, 0.5), Hold(0.5, 0.51));

        Assert.Empty(LookOverFindingBuilder.Build(Facts([], staged: [detection], live: [detection, fresh], successors: [(old, detection)])));
    }

    [Theory]
    [InlineData(0.5, 0.52, true)]
    [InlineData(0.5, 0.56, false)]
    public void SameSpot_MeansTheCentreLiesWithinTheLargerRadius_InPixelsOfThePhoto(double x, double y, bool same)
    {
        // 2000 x 1000: the radius (0.03 of the long side) is 60 px; 0.02 down is 20 px, 0.06 down is 60+ px with the x offset.
        var kept = Hold(0.5, 0.5);
        var fresh = Hold(x + 0.005, y);

        Assert.Equal(same, SameSpotFinder.Nearest(kept, [fresh], (2000, 1000)) is not null);
    }

    // "Greetings to Patrick": the purple sits on the old orange's outline, a brown neighbour's centre is nearer the old
    // hold's centre. The finding pairs the hold on the outline, not the nearest centre.
    [Fact]
    public void SameSpot_PairsTheHoldOnTheOldOutline_NotTheNearestCentre()
    {
        // 2000 x 1000 px: the old outline spans 1000..1120 x 460..540 px, right of the old centre (1000, 500).
        var (old, copy) = (Hold(0.5, 0.5), Hold(0.5, 0.5));
        copy.Radius = 0.04;
        copy.ShapePoints =
        [
            new() { Dx = 0, Dy = -0.04 }, new() { Dx = 0.06, Dy = -0.04 }, new() { Dx = 0.06, Dy = 0.04 }, new() { Dx = 0, Dy = 0.04 },
        ];
        var (purple, brown) = (Hold(0.535, 0.5), Hold(0.475, 0.5));
        (purple.Radius, brown.Radius) = (0.02, 0.02);

        var finding = Assert.Single(LookOverFindingBuilder.Build(Facts([], live: [copy, brown, purple], successors: [(old, copy)])));

        Assert.Equal(purple.Id, finding.OtherHoldId);

        // Nothing meets the old footprint: the nearest centre within the larger radius, as before.
        Assert.Equal(brown.Id, SameSpotFinder.Nearest(Hold(0.5, 0.5), [brown, Hold(0.54, 0.5)], (2000, 1000))?.Id);
    }

    // A small hold over the old centre keeps the spot against a neighbour that only clips the old circle's edge, even
    // though the neighbour's share of the old footprint is the larger one (both well under a real match).
    [Fact]
    public void SameSpot_AHoldOverTheOldCentre_BeatsANeighbourClippingTheEdge()
    {
        // 2000 x 1000 px: the old hold is 60 px round; the small one is 10 px on its centre, the neighbour 20 px, 50 px right.
        var (kept, onCentre, clipping) = (Hold(0.5, 0.5), Hold(0.5, 0.5), Hold(0.525, 0.5));
        (onCentre.Radius, clipping.Radius) = (0.005, 0.01);

        Assert.Equal(onCentre.Id, SameSpotFinder.Nearest(kept, [clipping, onCentre], (2000, 1000))?.Id);
    }

    [Fact]
    public void TheAnswers_FollowTheKindAndTheUsersRights_AndAHoldThatIsNotLiveCanOnlyBeAcknowledged()
    {
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.DifferentHold], LookOverRules.AnswersFor(LookOverFindingKind.UnsureMatch, true, true, true));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.RemoveHold], LookOverRules.AnswersFor(LookOverFindingKind.NotFound, true, true, false));
        Assert.Equal(
            [LookOverAnswer.Settle, LookOverAnswer.UseNewHold, LookOverAnswer.RemoveHold],
            LookOverRules.AnswersFor(LookOverFindingKind.SameSpot, true, true, true));
        Assert.Equal([LookOverAnswer.Settle], LookOverRules.AnswersFor(LookOverFindingKind.MatchedToHandPlaced, true, true, true));
        Assert.Equal([LookOverAnswer.Settle], LookOverRules.AnswersFor(LookOverFindingKind.ConflictingNew, false, true, false));
        Assert.Equal([LookOverAnswer.Settle], LookOverRules.AnswersFor(LookOverFindingKind.ReplacedByOther, false, true, true));

        // Somebody who may not edit holds on the wall (an installation admin who is not on it) acknowledges, nothing else.
        Assert.All(Enum.GetValues<LookOverFindingKind>(), k => Assert.Equal([LookOverAnswer.Settle], LookOverRules.AnswersFor(k, true, false, false)));
        Assert.Equal(Enum.GetValues<LookOverFindingKind>().Order(), LookOverRules.Order.Order());
    }

    // The mark on the live hold belongs to the finding only when the carry set it for the unanswered card on a hold
    // nothing else had marked, and nothing marks that hold afterwards.
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void TheFinding_OwnsTheHoldsMark_OnlyWhenTheCardSetItAndNothingElseMarksTheHold(bool cardMarked, bool unalignedPanel, bool secondOldHold, bool owns)
    {
        var (old, other, detection) = (Hold(0.5, 0.5), Hold(0.52, 0.5), Hold(0.5, 0.5));
        detection.NeedsReview = true;
        var card = new WallUpdateException { Kind = UpdateExceptionKind.LowConfidenceMatch, OldHoldId = old.Id, StagedHoldId = detection.Id };
        (Hold Old, Hold Live)[] successors = secondOldHold ? [(old, detection), (other, detection)] : [(old, detection)];

        var facts = Facts(
            [card], staged: [detection], live: [detection], successors: successors, carry: [new CarryoverDecision(old.Id, CarryKind.Carried, detection.Id)],
            cardMarked: cardMarked ? [old.Id] : [], unaligned: unalignedPanel ? [Panel] : []);

        Assert.Equal(owns, LookOverFindingBuilder.Build(facts).Single(f => f.OldHoldId == old.Id).FlaggedHold);
    }

    [Fact]
    public void AHoldNotFoundAgain_OrUnderANewHold_NeverOwnsTheMark_ThoughTheHoldIsMarked()
    {
        var (old, copy, fresh) = (Hold(0.5, 0.5), Hold(0.5, 0.5), Hold(0.51, 0.5));
        copy.NeedsReview = true;
        var sameSpot = Facts([], live: [copy, fresh], successors: [(old, copy)], cardMarked: [old.Id]);
        var notFound = Facts(
            [], live: [copy], successors: [(old, copy)], cardMarked: [old.Id],
            notFound: new() { [old.Id] = HoldMoveOutcome.NotFound }, boulders: new() { [copy.Id] = 1 });

        Assert.False(Assert.Single(LookOverFindingBuilder.Build(sameSpot)).FlaggedHold);
        Assert.False(Assert.Single(LookOverFindingBuilder.Build(notFound)).FlaggedHold);
    }

    [Fact]
    public void TwoCardsAboutOneOldHold_GiveOneFinding_OfTheMoreConsequentialKind_WhateverTheirOrder()
    {
        var (old, copy) = (Hold(0.5, 0.5), Hold(0.5, 0.5));
        var removed = new WallUpdateException { Kind = UpdateExceptionKind.PossiblyRemoved, OldHoldId = old.Id };
        var replaced = new WallUpdateException { Kind = UpdateExceptionKind.ReplacedByOther, OldHoldId = old.Id };

        foreach (var cards in new[] { new[] { removed, replaced }, new[] { replaced, removed } })
        {
            var finding = Assert.Single(LookOverFindingBuilder.Build(Facts(cards, live: [copy], successors: [(old, copy)])));
            Assert.Equal(LookOverFindingKind.ReplacedByOther, finding.Kind);
        }
    }

    private static Hold Hold(double x, double y) =>
        new() { WallId = Guid.Empty, WallPanelId = Panel, X = x, Y = y, Radius = 0.03, Generation = 3 };

    private static LookOverPromoteFacts Facts(
        IReadOnlyList<WallUpdateException> cards,
        Hold[]? staged = null,
        Hold[]? live = null,
        (Hold Old, Hold Live)[]? successors = null,
        CarryoverDecision[]? carry = null,
        Dictionary<Guid, HoldMoveOutcome>? notFound = null,
        Dictionary<Guid, int>? boulders = null,
        Guid[]? cardMarked = null,
        Guid[]? unaligned = null) =>
        new(
            cards,
            (carry ?? []).ToDictionary(d => d.OldHoldId),
            (successors ?? []).ToDictionary(s => s.Old.Id, s => s.Live.Id),
            (live ?? []).Distinct().ToDictionary(h => h.Id),
            (staged ?? []).ToDictionary(h => h.Id),
            notFound ?? [],
            boulders ?? [],
            new Dictionary<Guid, (int Width, int Height)> { [Panel] = (2000, 1000) },
            (cardMarked ?? []).ToHashSet(),
            (unaligned ?? []).ToHashSet(),
            new Dictionary<Guid, List<Guid>>());
}
