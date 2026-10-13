// <copyright file="LookOverQueueTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Web.Components.Shared.LookOver;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The look-over's card deck: one queue of everything open, what boulders use first and then group by group; the
/// chips above it count what each filter would ask about; an answered card leaves the queue without moving the rest.
/// </summary>
public class LookOverQueueTests
{
    private static readonly Guid Panel = Guid.NewGuid();
    private static readonly LookOverBoulder Crimp = new(Guid.NewGuid(), "Crimp line", true);
    private static readonly LookOverBoulder Warmup = new(Guid.NewGuid(), "Warm-up", false);

    [Fact]
    public void TheQueue_AsksWhatBouldersUseFirst_ThenGroupByGroup_MissingHoldsAhead()
    {
        var unsure = View(LookOverFindingKind.UnsureMatch);
        var unsureOnBoulder = View(LookOverFindingKind.UnsureMatch, Crimp);
        var replacedOnBoulder = View(LookOverFindingKind.ReplacedByOther, Warmup);
        var sameSpot = View(LookOverFindingKind.SameSpot);
        var missing = Missing(0.2, 0.2);
        var missingOnBoulder = Missing(0.8, 0.8, Crimp);
        var answered = View(LookOverFindingKind.UnsureMatch, Crimp) with { Status = LookOverFindingStatus.Settled };
        var listing = new LookOverListing();

        listing.Take([unsure, sameSpot, answered, unsureOnBoulder, missing, replacedOnBoulder, missingOnBoulder]);

        Assert.Equal(
            [missingOnBoulder.Id, replacedOnBoulder.Id, unsureOnBoulder.Id, missing.Id, unsure.Id, sameSpot.Id],
            listing.Queue(LookOverFilter.All, null).Select(f => f.Id));
        Assert.Equal([missingOnBoulder.Id, replacedOnBoulder.Id, unsureOnBoulder.Id], listing.Queue(LookOverFilter.OnBoulders, null).Select(f => f.Id));
        Assert.Equal([missingOnBoulder.Id, missing.Id], listing.Queue(LookOverFilter.Missing, null).Select(f => f.Id));
        Assert.Equal([unsureOnBoulder.Id, unsure.Id], listing.Queue(new LookOverFilter(Kind: LookOverFindingKind.UnsureMatch), null).Select(f => f.Id));
        Assert.Equal([missingOnBoulder.Id, unsureOnBoulder.Id], listing.Queue(LookOverFilter.All, Crimp.Id).Select(f => f.Id));
    }

    [Fact]
    public void AMissingHoldOfAKind_IsNotAmongThatKindsCards()
    {
        var card = View(LookOverFindingKind.NotFound, Crimp);
        var missing = Missing(0.5, 0.5);
        var listing = new LookOverListing();

        listing.Take([missing, card]);

        Assert.Equal([card.Id], listing.Queue(new LookOverFilter(Kind: LookOverFindingKind.NotFound), null).Select(f => f.Id));
        Assert.Equal([missing.Id], listing.Queue(LookOverFilter.Missing, null).Select(f => f.Id));
    }

    [Fact]
    public void TheChips_CountWhatIsOpenInEachFilter_AndLeaveOutEmptyGroups()
    {
        var listing = new LookOverListing();
        listing.Take(
        [
            View(LookOverFindingKind.UnsureMatch, Crimp), View(LookOverFindingKind.UnsureMatch), View(LookOverFindingKind.SameSpot), Missing(0.1, 0.1, Warmup), Missing(0.2, 0.2),
            View(LookOverFindingKind.NotFound) with { Status = LookOverFindingStatus.Settled },
        ]);

        var chips = listing.Chips(null);

        Assert.Equal(
            [("All", 5), ("On boulders", 2), ("Could not find", 2), ("Unsure matches", 2), ("Same spot", 1)],
            chips.Select(c => (c.Label, c.Open)));
        Assert.Equal(listing.Queue(chips[3].Filter, null).Count, chips[3].Open);
        Assert.Equal([("All", 1), ("Unsure matches", 1)], listing.Chips(Crimp.Id).Select(c => (c.Label, c.Open)));
    }

    [Fact]
    public void AnAnsweredCard_LeavesTheQueue_TheRestKeepsItsOrder_AndUndoPutsItBackInItsPlace()
    {
        var (first, second, third) = (View(LookOverFindingKind.UnsureMatch, Crimp), View(LookOverFindingKind.UnsureMatch, Crimp), View(LookOverFindingKind.UnsureMatch));
        var listing = new LookOverListing();
        listing.Take([first, second, third]);

        // The answer took the boulder's mark with it: the card that follows is still "on a boulder", as when the page opened.
        listing.Take([first with { Status = LookOverFindingStatus.Settled }, second with { Boulders = [] }, third]);

        Assert.Equal([second.Id, third.Id], listing.Queue(LookOverFilter.All, null).Select(f => f.Id));
        Assert.Equal([second.Id], listing.Queue(LookOverFilter.OnBoulders, null).Select(f => f.Id));

        listing.Take([first, second, third]);
        Assert.Equal([first.Id, second.Id, third.Id], listing.Queue(LookOverFilter.All, null).Select(f => f.Id));
    }

    [Fact]
    public void TheWords_NameTheGroupsShortly_AndTheFinishButtonSaysWhatStaysOpen()
    {
        Assert.Equal(Enum.GetValues<LookOverFindingKind>().Length, Enum.GetValues<LookOverFindingKind>().Select(LookOverCopy.ChipLabel).Distinct().Count());
        Assert.Equal("Could not find", LookOverCopy.QueueLabel(Missing(0.5, 0.5)));
        Assert.Equal("Unsure matches", LookOverCopy.QueueLabel(View(LookOverFindingKind.UnsureMatch)));
        Assert.Equal("Used by 7 boulders", LookOverCopy.UsedBy(7));
        Assert.Equal("Used by 1 boulder", LookOverCopy.UsedBy(1));
        Assert.Equal("Finish anyway — 12 open", LookOverCopy.FinishAnyway(12));
        Assert.StartsWith("1 finding is still open.", LookOverCopy.FinishAnywayQuestion(1));
        Assert.DoesNotContain("green", LookOverCopy.ReassignQuestion(Missing(0.5, 0.5, Crimp)));
        Assert.DoesNotContain("green", LookOverCopy.MoveQuestion(Missing(0.5, 0.5)));
    }

    private static LookOverFindingView Missing(double x, double y, params LookOverBoulder[] boulders) =>
        View(LookOverFindingKind.NotFound, boulders) with { HoldKept = true, KeptAtUpdate = true, PanelId = Panel, SpotX = x, SpotY = y };

    private static LookOverFindingView View(LookOverFindingKind kind, params LookOverBoulder[] boulders) =>
        new(Guid.NewGuid(), kind, LookOverFindingStatus.Open, null, 3, Guid.NewGuid(), Guid.NewGuid(), null, null, true, false, null, null, false, boulders, []);
}
