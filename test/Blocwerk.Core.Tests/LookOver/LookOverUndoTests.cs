// <copyright file="LookOverUndoTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// Undo and the order of answers: a boulder with two missing holds is cleared by whichever answer comes last, and
/// taking back EITHER answer marks it again. Undo is refused once it means nothing (the round is finished, the hold is
/// history), a bulk answer settles only what was confirmed, and every answer is one journal batch.
/// </summary>
public class LookOverUndoTests
{
    [Fact]
    public async Task ABoulderWithTwoMissingHolds_IsClearedByTheLastAnswer_AndUndoingTheFirstMarksItAgain()
    {
        using var h = new WallTestHarness();
        var (w, otherOld) = await ApplyTwoMissingAsync(h);
        var service = Service(h);
        var listed = await service.ListAsync(w.WallId);
        Assert.Equal([LookOverFindingKind.NotFound, LookOverFindingKind.NotFound], listed.Select(f => f.Kind));
        var first = listed.Single(f => f.OldHoldId == w.TargetId);
        var second = listed.Single(f => f.OldHoldId == otherOld);

        // Both findings come from the promote itself, and both say the boulder was marked for their hold.
        await using (var db = h.CreateContext())
        {
            Assert.All(await db.WallLookOverFindings.ToListAsync(), f => Assert.Equal([w.BoulderId], f.MarkedBoulderIds));
            Assert.Equal(2, await db.BoulderHoldMoves.CountAsync(m => m.Outcome == HoldMoveOutcome.NotFound));
        }

        await service.AnswerAsync(w.WallId, first.Id, LookOverAnswer.Settle);
        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        await service.AnswerAsync(w.WallId, second.Id, LookOverAnswer.Settle);
        Assert.False((await BoulderAsync(h, w.BoulderId)).NeedsReview);

        // The clearing is recorded on the second answer; undoing the FIRST must still mark the boulder again.
        await service.UndoAsync(w.WallId, first.Id);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        var reason = Assert.Single(await h.BoulderService.GetBoulderMovesAsync(w.BoulderId));
        Assert.Equal(first.HoldId, reason.NewHoldId);
        var after = (await service.ListAsync(w.WallId)).ToDictionary(f => f.Id, f => f.Status);
        Assert.Equal((LookOverFindingStatus.Open, LookOverFindingStatus.Settled), (after[first.Id], after[second.Id]));

        // And answering it again clears the boulder again.
        await service.AnswerAsync(w.WallId, first.Id, LookOverAnswer.Settle);
        Assert.False((await BoulderAsync(h, w.BoulderId)).NeedsReview);
    }

    [Fact]
    public async Task Undo_IsRefused_AndNotOffered_OnceTheRoundIsFinished()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);
        Assert.True(Assert.Single(await service.ListAsync(w.WallId)).CanUndo);

        await service.FinishAsync(w.WallId, leaveOpen: false);

        await Assert.ThrowsAsync<UserFacingException>(() => service.UndoAsync(w.WallId, view.Id));
        Assert.False((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.Equal(LookOverFindingStatus.Settled, (await FindingAsync(h, w.WallId)).Status);
    }

    [Fact]
    public async Task Undo_IsRefused_AndNotOffered_OnceALaterUpdateMadeTheHoldHistory()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        await ApplyAgainAsync(await StageAgainAsync(h, w), score: 0.4);

        var old = (await service.ListAsync(w.WallId)).Single(f => f.Id == view.Id);
        Assert.Equal((false, null), (old.CanUndo, old.HoldId));
        await Assert.ThrowsAsync<UserFacingException>(() => service.UndoAsync(w.WallId, view.Id));

        // The retained row of the earlier generation is not touched.
        Assert.False((await HoldAsync(h, w.DetectionId))!.NeedsReview);
    }

    [Fact]
    public async Task ABulkAnswer_SettlesOnlyWhatWasConfirmed_SaysWhatWasAnsweredMeanwhile_AndEachCanBeUndone()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var later = await AddFindingAsync(h, new() { WallId = w.WallId, Generation = 3, Kind = LookOverFindingKind.UnsureMatch });
        var service = Service(h);
        var onBoulder = (await service.ListAsync(w.WallId)).Single(f => f.Boulders.Count == 1);
        await using (var db = h.CreateContext())
        {
            // The boulder is marked, and the update's record says: for a hold of this update only.
            (await db.WallLookOverFindings.SingleAsync(f => f.Id == onBoulder.Id)).MarkedBoulderIds = [w.BoulderId];
            (await db.Boulders.SingleAsync(b => b.Id == w.BoulderId)).NeedsReview = true;
            await db.SaveChangesAsync();
        }

        // Confirmed with one finding on the page; a second one that appeared since is not part of the answer.
        var result = await service.SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, [onBoulder.Id, Guid.NewGuid()], onlyWithoutBoulders: false);

        Assert.Equal(new LookOverBulkResult(1, 2), result);
        var after = (await service.ListAsync(w.WallId)).ToDictionary(f => f.Id);
        Assert.Equal((LookOverFindingStatus.Settled, LookOverFindingStatus.Open), (after[onBoulder.Id].Status, after[later].Status));
        Assert.False((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.Equal((1, true), (after[onBoulder.Id].ClearedBoulders, after[onBoulder.Id].CanUndo));
        Assert.Equal(new LookOverBulkResult(0, 1), await service.SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, [onBoulder.Id], onlyWithoutBoulders: false));

        await service.UndoAsync(w.WallId, onBoulder.Id);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.True((await HoldAsync(h, w.DetectionId))!.NeedsReview);
    }

    [Fact]
    public async Task EveryAnswerIsOneJournalBatch_AndAMergeKeepsItsOwnBatchInside()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);
        await service.UndoAsync(w.WallId, view.Id);
        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId]);

        await using var db = h.CreateContext();
        var batches = await db.ChangeJournalBatches.Where(b => b.ScopeId == w.WallId).ToListAsync();

        // Settling took the card's mark off the hold and undoing put it back: two answers, two batches.
        Assert.Equal(2, batches.Count(b => b.Label == WallLookOverService.BatchLabel));
        var merge = Assert.Single(batches, b => b.Label == HoldDuplicateService.BatchLabel);
        Assert.Equal(merge.Id, (await FindingAsync(h, w.WallId)).MergeBatchId);
        Assert.True(await db.ChangeJournalEntries.AnyAsync(e => e.BatchId == merge.Id && e.EntityType == nameof(BoulderHold)));
    }

    [Fact]
    public async Task WhenTheHoldOperationRefuses_TheFindingIsOpenAgain_AndCanBeAnsweredAfterwards()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var failing = Substitute.For<IWallService>();
        failing.DeleteHoldAsync(default).ThrowsAsyncForAnyArgs(new InvalidOperationException("The hold could not be deleted."));
        var view = Assert.Single(await Service(h).ListAsync(w.WallId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(h, walls: failing).AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [w.BoulderId]));

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingStatus.Open, null), (finding.Status, finding.Answer));
        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [w.BoulderId]);
        Assert.Null(await HoldAsync(h, view.HoldId));
    }
}
