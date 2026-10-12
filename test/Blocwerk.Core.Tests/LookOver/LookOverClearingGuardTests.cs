// <copyright file="LookOverClearingGuardTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// "Still there" leaves a boulder marked whenever something else may be why it is marked, also when that something left
/// no reason row: two of its holds that became one at the update, a duplicate merge since, a marked or changed hold, a
/// hold taken off or replaced.
/// </summary>
public class LookOverClearingGuardTests
{
    [Fact]
    public async Task ABoulderWhoseTwoOtherHoldsBecameOneAtTheUpdate_IsNotRecordedAsMarkedForTheMissingHold_AndStaysMarked()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: false);
        w.Probe.Set(TargetX, TargetY, 0.2);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        await using (var db = h.CreateContext())
        {
            // The boulder also uses two anchors, and the update carries both onto ONE detection: marked, with no reason row.
            var olds = await db.Holds.Where(x => x.Generation == 2 && x.Id != w.TargetId).OrderBy(x => x.X).ThenBy(x => x.Y).Take(2).ToListAsync();
            db.BoulderHolds.AddRange(olds.Select(o => new BoulderHold { BoulderId = w.BoulderId, HoldId = o.Id }));
            var decisions = await db.WallUpdateHoldDecisions
                .Where(d => d.Kind == WallUpdateHoldDecisionKind.Carry && (d.HoldId == olds[0].Id || d.HoldId == olds[1].Id))
                .OrderBy(d => d.HoldId == olds[0].Id ? 0 : 1)
                .ToListAsync();
            decisions[1].PairedHoldId = decisions[0].PairedHoldId;
            await db.SaveChangesAsync();
        }

        await PromoteLikeApplyAsync(w, session);

        var service = Service(h);
        var view = (await service.ListAsync(w.WallId)).Single(f => f.Kind == LookOverFindingKind.NotFound);
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((true, 2), (boulder.NeedsReview, boulder.BoulderHolds.Count));
        await using (var db = h.CreateContext())
        {
            Assert.Equal([], (await db.WallLookOverFindings.SingleAsync(f => f.Id == view.Id)).MarkedBoulderIds);
        }

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
    }

    [Fact]
    public async Task ABoulderWhoseHoldsWereMergedAsDuplicatesSinceTheUpdate_StaysMarked()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Guid keep;
        Guid extra;
        await using (var db = h.CreateContext())
        {
            // The boulder uses two more holds of the live photo, which somebody then merges in the duplicate review.
            var live = await db.Holds.Where(x => x.Generation == 3 && x.Id != view.HoldId).OrderBy(x => x.X).ThenBy(x => x.Y).FirstAsync();
            var twin = new Hold { WallId = w.WallId, WallPanelId = live.WallPanelId, X = live.X, Y = live.Y, Radius = live.Radius, Generation = 3 };
            db.Holds.Add(twin);
            db.BoulderHolds.AddRange(
                new BoulderHold { BoulderId = w.BoulderId, HoldId = live.Id }, new BoulderHold { BoulderId = w.BoulderId, HoldId = twin.Id });
            live.NeedsReview = false;
            await db.SaveChangesAsync();
            (keep, extra) = (live.Id, twin.Id);
        }

        await Duplicates(h).MergeAsync(w.WallId, keep, extra, HoldMergeMode.KeepLeft);

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.Equal(0, Assert.Single(await service.ListAsync(w.WallId)).ClearedBoulders);
    }

    [Theory]
    [InlineData("flagged hold")]
    [InlineData("changed hold")]
    [InlineData("hold taken off")]
    [InlineData("hold replaced")]
    public async Task ABoulderWithSomethingElseToCheck_StaysMarked(string other)
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        await using (var db = h.CreateContext())
        {
            var second = await db.Holds.Where(x => x.Generation == 3 && x.Id != view.HoldId).OrderBy(x => x.X).ThenBy(x => x.Y).FirstAsync();
            db.BoulderHolds.Add(new BoulderHold { BoulderId = w.BoulderId, HoldId = second.Id });
            second.NeedsReview = other == "flagged hold";
            if (other == "changed hold")
            {
                db.HoldGenerationLinks.Add(new HoldGenerationLink
                {
                    WallId = w.WallId, NewHoldId = second.Id, Kind = HoldGenerationLinkKind.Changed, FromGeneration = 2, ToGeneration = 3,
                });
            }
            else if (other is "hold taken off" or "hold replaced")
            {
                db.BoulderHoldMoves.Add(new BoulderHoldMove
                {
                    WallId = w.WallId, BoulderId = w.BoulderId, NewHoldId = second.Id, FromGeneration = 2, ToGeneration = 3,
                    Outcome = other == "hold replaced" ? HoldMoveOutcome.Replaced : HoldMoveOutcome.Removed,
                });
            }

            await db.SaveChangesAsync();
        }

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
    }

    [Fact]
    public async Task TheCountsOfTheNotice_AreOnlyWorkedOutForWhoMayLookOver()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        Assert.Equal(1, (await Service(h).GetStateAsync(w.WallId))!.BouldersMarked);

        h.ActingUser = await AddMemberAsync(h, w.WallId, WallRole.Member);

        var state = (await Service(h).GetStateAsync(w.WallId))!;
        Assert.Equal((true, false, 0, 0), (state.Pending, state.CanLookOver, state.BouldersMarked, state.MoreBoulders));
    }

    [Fact]
    public async Task TheStoredSpotsTexturePicture_IsAskedForOnce_NotAgainForTheLargeView()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var findingId = (await FindingAsync(h, w.WallId)).Id;

        // No texture here: there is no picture, and "none" is the answer both times without failing.
        Assert.Null(await Service(h).GetCropAsync(w.WallId, findingId, CheckCropView.Model));
        Assert.Null(await Service(h).GetCropAsync(w.WallId, findingId, CheckCropView.Model));
    }
}
