// <copyright file="UpdateReviewBuilderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The review's items worked out from the session's facts: a hold a card is about is asked about on its card only, a
/// removal off the centre is offered only where the promote keeps it, and "open" counts exactly what the look-over will ask.
/// </summary>
public class UpdateReviewBuilderTests
{
    private static readonly Guid Centre = Guid.NewGuid();
    private static readonly Guid Right = Guid.NewGuid();

    [Theory]
    [InlineData(UpdateExceptionStatus.Pending, true)]
    [InlineData(UpdateExceptionStatus.Kept, false)]
    [InlineData(UpdateExceptionStatus.NotSame, true)]
    public void AnUnsureMatch_StaysOpen_UnlessConfirmed(UpdateExceptionStatus status, bool open)
    {
        var old = Guid.NewGuid();
        var staged = Staged(Centre);
        var card = new WallUpdateException { Kind = UpdateExceptionKind.LowConfidenceMatch, OldHoldId = old, StagedHoldId = staged.Id, Status = status };

        // As the answer writes it: "not the same hold" takes the pair apart.
        var twin = status == UpdateExceptionStatus.NotSame ? (Guid?)null : staged.Id;
        var items = UpdateReviewBuilder.Build(Facts([card], new() { [old] = new(CarryKind.Carried, twin, false, null) }, [staged], old));

        var item = Assert.Single(items);
        Assert.Equal((ReviewGroup.Unsure, open), (item.Group, item.IsOpen));
        Assert.Equal([ReviewAnswer.Keep, ReviewAnswer.NotSame], item.Answers);
        Assert.Equal(open ? 1 : 0, ReviewCounts.Of(items).Open);
    }

    [Theory]
    [InlineData(UpdateExceptionKind.LowConfidenceMatch, UpdateExceptionStatus.NotSame)]
    [InlineData(UpdateExceptionKind.MatchedToHandPlaced, UpdateExceptionStatus.Removed)]
    public void ACardWhosePairAStepByStepSaveRestored_ReadsSameHold_AndLeavesNoFinding(UpdateExceptionKind kind, UpdateExceptionStatus status)
    {
        var old = Guid.NewGuid();
        var staged = Staged(Centre);
        var card = new WallUpdateException { Kind = kind, OldHoldId = old, StagedHoldId = staged.Id, Status = status };

        var item = Assert.Single(UpdateReviewBuilder.Build(Facts([card], new() { [old] = new(CarryKind.Carried, staged.Id, false, null) }, [staged], old)));

        Assert.Equal((ReviewAnswer.Keep, false), (item.Answer!.Value, item.IsOpen));
    }

    [Fact]
    public void AReplacedHoldOffTheCentre_TakesNoRemoval_AndItsDetectionIsNotAskedTwice()
    {
        var old = Guid.NewGuid();
        var staged = Staged(Right);
        var card = new WallUpdateException { Kind = UpdateExceptionKind.ReplacedByOther, OldHoldId = old, StagedHoldId = staged.Id, PanelId = Right };

        var items = UpdateReviewBuilder.Build(
            Facts([card], new() { [old] = new(CarryKind.Carried, null, false, null) }, [staged], old, oldPanel: Right, onCentre: false));

        var item = Assert.Single(items);
        Assert.Equal((ReviewGroup.Replaced, Right), (item.Group, item.PanelId));
        Assert.Equal([ReviewAnswer.Keep], item.Answers);
    }

    [Fact]
    public void AKeptHold_IsOpenUntilConfirmed_AndAConfirmedOneReadsStillThere()
    {
        var (kept, confirmed, removed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var carry = new Dictionary<Guid, CarryFact>
        {
            [kept] = new(CarryKind.Carried, null, false, null),
            [confirmed] = new(CarryKind.Carried, null, true, null),
            [removed] = new(CarryKind.Removed, null, true, null),
        };

        var items = UpdateReviewBuilder.Build(Facts([], carry, [], kept, confirmed, removed));

        Assert.Equal(3, items.Count(i => i.Group == ReviewGroup.NotFound));
        Assert.Null(items.Single(i => i.OldHoldId == kept).Answer);
        Assert.Equal(ReviewAnswer.StillThere, items.Single(i => i.OldHoldId == confirmed).Answer);
        Assert.Equal(ReviewAnswer.Gone, items.Single(i => i.OldHoldId == removed).Answer);
        Assert.Equal((1, 1, 2), (ReviewCounts.Of(items).Open, ReviewCounts.Of(items).ToCheck, ReviewCounts.Of(items).Answered));
    }

    [Fact]
    public void ALeftOutDetection_OnAHoldThatGoesLive_IsNoLaterFinding()
    {
        var live = Staged(Centre) with { Radius = 0.05 };
        var covered = Staged(Centre) with { Id = Guid.NewGuid(), DiscardReason = nameof(NewHoldDiscardReason.InsideCarriedHold), Radius = 0.04 };
        var bare = Staged(Centre) with { Id = Guid.NewGuid(), X = 0.9, Y = 0.9, DiscardReason = nameof(NewHoldDiscardReason.OnMarker) };

        var items = UpdateReviewBuilder.Build(Facts([], [], [live, covered, bare]) with
        {
            NewCentre = new Dictionary<Guid, bool> { [covered.Id] = true, [bare.Id] = true },
        });

        Assert.Equal(ReviewGroup.NewHold, items.Single(i => i.StagedHoldId == live.Id).Group);
        Assert.False(items.Single(i => i.StagedHoldId == covered.Id).LeavesFinding);
        Assert.True(items.Single(i => i.StagedHoldId == bare.Id).LeavesFinding);
    }

    private static StagedFact Staged(Guid panel) => new(Guid.NewGuid(), panel, 0.5, 0.5, 0.02, 0.95, null, null, false);

    private static UpdateReviewFacts Facts(
        List<WallUpdateException> cards, Dictionary<Guid, CarryFact> carry, List<StagedFact> staged, params Guid[] oldIds) =>
        Facts(cards, carry, staged, oldIds, Centre, true);

    private static UpdateReviewFacts Facts(
        List<WallUpdateException> cards, Dictionary<Guid, CarryFact> carry, List<StagedFact> staged, Guid old, Guid oldPanel, bool onCentre) =>
        Facts(cards, carry, staged, [old], oldPanel, onCentre);

    private static UpdateReviewFacts Facts(
        List<WallUpdateException> cards, Dictionary<Guid, CarryFact> carry, List<StagedFact> staged, Guid[] oldIds, Guid oldPanel, bool onCentre) =>
        new(
            [new ReviewPanel(Centre, 0, 0, "Centre panel"), new ReviewPanel(Right, 1, 0, "Right panel")],
            Centre,
            cards,
            carry,
            new Dictionary<Guid, bool>(),
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            staged,
            oldIds.ToDictionary(id => id, id => new OldHoldFact(id, "grey", oldPanel, onCentre)),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new Dictionary<(Guid, ReviewSubject), WallUpdateReviewAnswer>(),
            [],
            new Dictionary<Guid, (int, int)> { [Centre] = (1000, 1000), [Right] = (1000, 1000) },
            new Dictionary<Guid, string>());
}
