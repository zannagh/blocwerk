// <copyright file="LookOverMissingListingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Web.Components.Shared.LookOver;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The page's section "Holds the update could not find": such a hold is asked about there and is not a card, the
/// holds are asked in an order that puts boulders first and neighbours next to each other, and the words say what an
/// answer did.
/// </summary>
public class LookOverMissingListingTests
{
    private static readonly Guid Panel = Guid.NewGuid();
    private static readonly LookOverBoulder Crimp = new(Guid.NewGuid(), "Crimp line", true);
    private static readonly LookOverBoulder Warmup = new(Guid.NewGuid(), "Warm-up", false);

    [Fact]
    public void TheHolds_AreAskedBouldersFirst_ThenThoseUnderANewHold_ThenRowByRowAlongThePhoto()
    {
        var lowRight = Missing(0.9, 0.82);
        var topRight = Missing(0.8, 0.1);
        var topLeft = Missing(0.2, 0.12);
        var covered = Missing(0.5, 0.9) with { Covered = true, OtherHoldId = Guid.NewGuid() };
        var onOne = Missing(0.5, 0.5, Crimp);
        var onTwo = Missing(0.6, 0.95, Crimp, Warmup);
        var card = View(LookOverFindingKind.UnsureMatch, Crimp);
        var listing = new LookOverListing();

        listing.Take([lowRight, card, topRight, covered, onOne, topLeft, onTwo]);

        Assert.Equal([onTwo.Id, onOne.Id, covered.Id, topLeft.Id, topRight.Id, lowRight.Id], listing.Missing(null).Select(f => f.Id));
        Assert.Equal([card.Id], listing.Cards.Select(f => f.Id));
        Assert.Equal([onTwo.Id], listing.Missing(Warmup.Id).Select(f => f.Id));
        Assert.Equal([LookOverFindingKind.UnsureMatch], listing.Kinds(onBouldersOnly: false, boulderId: null));
    }

    [Fact]
    public void AnAnsweredHold_LeavesTheQueue_TheOthersKeepTheirOrder_AndItIsListedAsAnswered()
    {
        var (first, second, third) = (Missing(0.1, 0.1), Missing(0.5, 0.1), Missing(0.9, 0.1));
        var listing = new LookOverListing();
        listing.Take([first, second, third]);

        var confirmed = second with { Status = LookOverFindingStatus.Settled, Answer = LookOverAnswer.Settle, HoldKept = false, CanUndo = true };
        var closed = third with { Status = LookOverFindingStatus.Closed, ClosedReason = LookOverCloseReason.RemovedInEditor, HoldKept = false, HoldId = null };
        listing.Take([first, confirmed, closed]);

        Assert.Equal([first.Id], listing.Missing(null).Select(f => f.Id));
        Assert.Equal([third.Id, second.Id], listing.MissingAnswered(null).Select(f => f.Id));
        Assert.Empty(listing.Cards);

        // Undone: back where it was, not at the end.
        listing.Take([first, second, closed]);
        Assert.Equal([first.Id, second.Id], listing.Missing(null).Select(f => f.Id));
    }

    [Fact]
    public void AnOpenFindingAboutAHoldThatIsNoLongerSuchAHold_IsACard()
    {
        // Its photo was taken again: the row is history, the finding can only be acknowledged, as a card.
        var stale = Missing(0.5, 0.5) with { HoldKept = false, HoldId = null };
        var listing = new LookOverListing();

        listing.Take([stale]);

        Assert.Empty(listing.Missing(null));
        Assert.Equal([stale.Id], listing.Cards.Select(f => f.Id));
    }

    [Fact]
    public void TheWords_SayWhatWasDone_AndHowManyHoldsABulkRemovalTakes()
    {
        var finding = Missing(0.5, 0.5, Crimp);
        string Answered(LookOverAnswer answer) => LookOverCopy.AnsweredLabel(finding with { Status = LookOverFindingStatus.Settled, Answer = answer });

        Assert.Equal("Is a hold on the new photo", Answered(LookOverAnswer.Reassign));
        Assert.Equal("Moved to where it is", Answered(LookOverAnswer.Moved));
        Assert.Equal("Still here", Answered(LookOverAnswer.Settle));
        Assert.Equal(
            "Hold merged in the duplicate review",
            LookOverCopy.AnsweredLabel(finding with { Status = LookOverFindingStatus.Closed, ClosedReason = LookOverCloseReason.MergedInEditor }));
        Assert.Equal("Not there any more: the 24 holds under a new hold that no boulder uses", LookOverCopy.BulkRemoveLabel(24, onlyCovered: true));
        Assert.Equal("Not there any more: all 131 holds that no boulder uses", LookOverCopy.BulkRemoveLabel(131, onlyCovered: false));
        Assert.Contains("1 boulder (Crimp line) uses the tapped hold", LookOverCopy.ReassignQuestion(finding));
        Assert.Equal("3 of 5 holds removed; the others were answered meanwhile, or a boulder uses them by now.", LookOverCopy.BulkRemoved(new(3, 5)));
        Assert.EndsWith(
            "61 holds were not found on the new photos and are not drawn on the wall.",
            LookOverCopy.BannerHint(new WallLookOverState(true, DateTimeOffset.UtcNow, 3, 80, 80, null, null, true, 4, 0, 61)));
    }

    private static LookOverFindingView Missing(double x, double y, params LookOverBoulder[] boulders) =>
        View(LookOverFindingKind.NotFound, boulders) with { HoldKept = true, KeptAtUpdate = true, PanelId = Panel, SpotX = x, SpotY = y };

    private static LookOverFindingView View(LookOverFindingKind kind, params LookOverBoulder[] boulders) =>
        new(Guid.NewGuid(), kind, LookOverFindingStatus.Open, null, 3, Guid.NewGuid(), Guid.NewGuid(), null, null, true, false, null, null, false, boulders, []);
}
