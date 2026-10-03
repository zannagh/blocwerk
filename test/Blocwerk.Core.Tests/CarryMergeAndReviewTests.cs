// <copyright file="CarryMergeAndReviewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.PanelUpdateCarryFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The promote's review signals around merges, blind carries and overridden deletes: what a person must
/// be shown because the carry made a call on their behalf.
/// </summary>
public class CarryMergeAndReviewTests
{
    // Two of a boulder's holds merged onto one successor: the membership keeps the more prominent mark (the
    // top must not become a plain hold) and the boulder is flagged for review.
    [Fact]
    public async Task Merge_KeepsTheTopMark_AndFlagsTheBoulder()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        Guid second;
        Guid boulderId;
        await using (var db = h.CreateContext())
        {
            var hold = new Hold { WallId = w.WallId, WallPanelId = w.PanelIds[0], X = 0.32, Y = 0.40, Radius = 0.02, Generation = 2 };
            var boulder = new Boulder { WallId = w.WallId, Name = "Merge", CreatedByUserId = h.Owner.Id, Generation = 2 };
            db.Holds.Add(hold);
            db.Boulders.Add(boulder);
            db.BoulderHolds.AddRange(
                new BoulderHold { BoulderId = boulder.Id, HoldId = w.CentreHoldId, Type = HoldType.Normal },
                new BoulderHold { BoulderId = boulder.Id, HoldId = hold.Id, Type = HoldType.Top });
            await db.SaveChangesAsync();
            second = hold.Id;
            boulderId = boulder.Id;
        }

        var s = await StageAsync(h, w.WallId, 3, 0);
        await Service(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId),
            new CarryoverDecision(second, CarryKind.Carried, s[0].HoldId)));

        await using var read = h.CreateContext();
        var membership = await read.BoulderHolds.SingleAsync(bh => bh.BoulderId == boulderId);
        Assert.Equal(s[0].HoldId, membership.HoldId);
        Assert.Equal(HoldType.Top, membership.Type);
        Assert.True((await read.Boulders.SingleAsync(b => b.Id == boulderId)).NeedsReview);
    }

    // An old hold nobody decided on is still carried (nothing is lost) but flagged for a person to check.
    [Fact]
    public async Task UndecidedOldHold_IsCarriedAndFlaggedForReview()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        var s = await StageAsync(h, w.WallId, 3, 0, 1);

        await Service(h).PromoteAsync(w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId)));

        await using var db = h.CreateContext();
        var successorId = (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.NeighbourHoldId)).NewHoldId!.Value;
        Assert.True((await db.Holds.SingleAsync(x => x.Id == successorId)).NeedsReview);
        Assert.False((await db.Holds.SingleAsync(x => x.Id == s[0].HoldId)).NeedsReview);
    }

    // In the big update the overlap step's "Delete hold" targets the staged CENTRE hold of a pair. One the
    // carry used as a successor is kept (and flagged); a new centre hold the review kept is still deleted.
    [Fact]
    public async Task OverlapDelete_KeepsACentreTwin_ButStillDeletesAKeptNewCentreHold()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        var s = await StageAsync(h, w.WallId, 3, 0, 1);
        Guid extra;
        await using (var db = h.CreateContext())
        {
            var hold = new Hold
            {
                WallId = w.WallId, WallPanelId = s[0].PanelId, X = 0.70, Y = 0.70, Radius = 0.02,
                Generation = 3, IsAutoDetected = true, NeedsReview = true,
            };
            db.Holds.Add(hold);
            await db.SaveChangesAsync();
            extra = hold.Id;
        }

        await Service(h).PromoteAsync(w.WallId, new BigUpdateConfirmation(
            [
                new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId),
                new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s[1].HoldId),
            ],
            [extra],
            [],
            [new NeighbourLinkSet(s[1].PanelId, [], [s[0].HoldId, extra])]));

        await using var read = h.CreateContext();
        var twin = await read.Holds.SingleAsync(x => x.Id == s[0].HoldId);
        Assert.Equal(3, twin.Generation);
        Assert.True(twin.NeedsReview);
        Assert.False(await read.Holds.AnyAsync(x => x.Id == extra));
    }
}
