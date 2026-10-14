// <copyright file="LookOverBoulderClearingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// A false alarm clears the boulder: "Still there" and "Same hold" say the update's reason was wrong, so a boulder that
/// was marked only for that hold loses its mark. Anything else about the boulder keeps it marked, a boulder that was
/// marked before the update is never touched, and undoing the answer puts everything back.
/// </summary>
public class LookOverBoulderClearingTests
{
    [Fact]
    public async Task ThePromote_RemembersWhichBouldersItMarked_AndWhichWereMarkedAlready()
    {
        using var fresh = new WallTestHarness();
        var w = await ApplyAsync(fresh, targetDetected: false, score: 0.2);
        Assert.Equal([w.BoulderId], (await FindingAsync(fresh, w.WallId)).MarkedBoulderIds);

        using var marked = new WallTestHarness();
        var before = await ApplyAsync(marked, targetDetected: false, score: 0.2, boulderMarkedBefore: true);
        Assert.Equal([], (await FindingAsync(marked, before.WallId)).MarkedBoulderIds);
    }

    [Fact]
    public async Task StillThere_ClearsTheBoulderThatWasOnlyMarkedForThatHold_AndUndoMarksItAgain()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Single(await h.BoulderService.GetBoulderMovesAsync(w.BoulderId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.False((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        var settled = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((1, true, "Owner", 1), (settled.ClearedBoulders, settled.CanUndo, settled.AnsweredBy, settled.AnswerBoulders));
        await using (var db = h.CreateContext())
        {
            Assert.Equal(HoldMoveOutcome.Stayed, (await db.BoulderHoldMoves.SingleAsync()).RemeasuredOutcome);
        }

        await service.UndoAsync(w.WallId, view.Id);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        var reopened = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((LookOverFindingStatus.Open, 0, false), (reopened.Status, reopened.ClearedBoulders, reopened.CanUndo));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.RemoveHold], reopened.Answers);
        var reason = Assert.Single(await h.BoulderService.GetBoulderMovesAsync(w.BoulderId));
        Assert.Equal(HoldMoveOutcome.NotFound, reason.Outcome);
        await using var after = h.CreateContext();
        Assert.Null((await after.BoulderHoldMoves.SingleAsync()).RemeasuredOutcome);
    }

    [Fact]
    public async Task ABoulderThatWasMarkedBeforeTheUpdate_StaysMarked()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2, boulderMarkedBefore: true);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.Equal(0, Assert.Single(await service.ListAsync(w.WallId)).ClearedBoulders);
    }

    [Fact]
    public async Task ABoulderWithAnotherOpenFindingOnOneOfItsHolds_StaysMarked_UntilThatOneIsAnsweredToo()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        var (otherHold, panel) = await AddSecondHoldToBoulderAsync(h, w);
        var other = await AddFindingAsync(h, new WallLookOverFinding
        {
            WallId = w.WallId, Generation = 3, Kind = LookOverFindingKind.UnsureMatch, HoldId = otherHold, PanelId = panel, MarkedBoulderIds = [w.BoulderId],
        });

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);
        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);

        // "Same hold" on the other one withdraws the last thing that kept the boulder marked.
        await service.AnswerAsync(w.WallId, other, LookOverAnswer.Settle);
        Assert.False((await BoulderAsync(h, w.BoulderId)).NeedsReview);
    }

    [Fact]
    public async Task ABoulderWithAnotherReasonFromTheUpdate_StaysMarked()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        var (otherHold, _) = await AddSecondHoldToBoulderAsync(h, w);
        await using (var db = h.CreateContext())
        {
            db.BoulderHoldMoves.Add(new BoulderHoldMove
            {
                WallId = w.WallId, BoulderId = w.BoulderId, NewHoldId = otherHold, DistanceMm = 60, Outcome = HoldMoveOutcome.Kept, FromGeneration = 2, ToGeneration = 3,
            });
            await db.SaveChangesAsync();
        }

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);

        // The boulder page no longer gives "not found again" as a reason, only the move.
        Assert.Equal(HoldMoveOutcome.Kept, Assert.Single(await h.BoulderService.GetBoulderMovesAsync(w.BoulderId)).Outcome);
    }

    [Fact]
    public async Task KeepOldHoldHere_OnAReplacedHold_DoesNotClearTheBoulder_TheHoldSituationDidChange()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        // A replaced hold explains no boulder's mark that could be taken back: the boulder is its setter's to check.
        Assert.Equal([], (await FindingAsync(h, w.WallId)).MarkedBoulderIds);

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.Equal(0, Assert.Single(await service.ListAsync(w.WallId)).ClearedBoulders);
    }

    [Theory]
    [InlineData(LookOverFindingKind.UnsureMatch, true)]
    [InlineData(LookOverFindingKind.NotFound, true)]
    [InlineData(LookOverFindingKind.ReplacedByOther, false)]
    [InlineData(LookOverFindingKind.PossiblyRemoved, false)]
    [InlineData(LookOverFindingKind.SameSpot, false)]
    [InlineData(LookOverFindingKind.MatchedToHandPlaced, false)]
    public void OnlyAnAnswerThatSaysTheUpdateWasWrong_WithdrawsTheReason(LookOverFindingKind kind, bool withdraws) =>
        Assert.Equal(withdraws, LookOverRules.SettlingWithdrawsTheReason(kind));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SameHold_ClearsABoulderTheUpdateMarkedForThatHold_ButNotWhenItIsUnknownWhoMarkedIt(bool knownMarkedByUpdate, bool staysMarked)
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        await using (var db = h.CreateContext())
        {
            // As a finding written before the marked boulders were recorded (null), or one that knows the update marked it.
            var finding = await db.WallLookOverFindings.SingleAsync();
            finding.MarkedBoulderIds = knownMarkedByUpdate ? [w.BoulderId] : null;
            (await db.Boulders.SingleAsync(b => b.Id == w.BoulderId)).NeedsReview = true;
            await db.SaveChangesAsync();
        }

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.Equal(staysMarked, (await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.False((await HoldAsync(h, w.DetectionId))!.NeedsReview);
    }

    [Fact]
    public async Task Undo_PutsTheHoldsMarkBack_AndIsOnlyForAnswersThatChangedNothingOnTheWall()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        await Assert.ThrowsAsync<UserFacingException>(() => service.UndoAsync(w.WallId, view.Id));
        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);
        Assert.False((await HoldAsync(h, w.DetectionId))!.NeedsReview);

        await service.UndoAsync(w.WallId, view.Id);

        Assert.True((await HoldAsync(h, w.DetectionId))!.NeedsReview);
        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingStatus.Open, null, null, false), (finding.Status, finding.Answer, finding.AnsweredByUserId, finding.ClearedHoldMark));

        // A removed hold is not brought back from here.
        var again = Assert.Single(await service.ListAsync(w.WallId));
        await service.AnswerAsync(w.WallId, again.Id, LookOverAnswer.DifferentHold, [w.BoulderId]);
        await service.AnswerAsync(w.WallId, again.Id, LookOverAnswer.RemoveHold, [w.BoulderId]);
        var removed = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((false, 1), (removed.CanUndo, removed.AnswerBoulders));
        await Assert.ThrowsAsync<UserFacingException>(() => service.UndoAsync(w.WallId, again.Id));
        var otherWall = await AddOtherWallAsync(h);
        await Assert.ThrowsAsync<UserFacingException>(() => service.UndoAsync(otherWall, again.Id));
    }

    /// <summary>Puts the boulder on one more live hold (an anchor); returns that hold and the live panel.</summary>
    private static async Task<(Guid HoldId, Guid PanelId)> AddSecondHoldToBoulderAsync(WallTestHarness h, LookWall w)
    {
        await using var db = h.CreateContext();
        var panel = await db.WallPanels.SingleAsync(p => p.WallId == w.WallId && p.Generation == 3);
        var anchor = await db.Holds.FirstAsync(x => x.WallPanelId == panel.Id && x.Generation == 3 && Math.Abs(x.X - Anchors[0].X) < Tolerance && Math.Abs(x.Y - Anchors[0].Y) < Tolerance);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = w.BoulderId, HoldId = anchor.Id });
        anchor.NeedsReview = false;
        await db.SaveChangesAsync();
        return (anchor.Id, panel.Id);
    }
}
