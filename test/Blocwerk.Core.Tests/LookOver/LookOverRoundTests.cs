// <copyright file="LookOverRoundTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// Rounds: a look-over starts with the first update that leaves a finding and ends with Finish. What one round settled
/// never comes back with the next update, an update that leaves nothing does not end a round by itself, and the
/// look-over step can neither fail a promote nor leave a finding answerable about a hold that became history.
/// </summary>
public class LookOverRoundTests
{
    [Fact]
    public async Task ASecondUpdateAfterAFinishedRound_ListsAndCountsOnlyItsOwnFindings()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        await service.SettleUnsureMatchesAsync(w.WallId);
        await service.FinishAsync(w.WallId, leaveOpen: false);
        var finishedAt = (await WallAsync(h, w.WallId)).LookOverCompletedAt;

        await ApplyAgainAsync(await StageAgainAsync(h, w), score: 0.4);

        var state = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((true, (int?)4, 1, 1), (state.Pending, state.Generation, state.OpenFindings, state.TotalFindings));
        var listed = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((4, LookOverFindingStatus.Open), (listed.Generation, listed.Status));

        // The last finished look-over stays on record while the next one is pending.
        Assert.Equal((finishedAt, "Owner"), (state.CompletedAt, state.CompletedBy));
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallLookOverFindings.CountAsync());
    }

    [Fact]
    public async Task AnUpdateDuringAnUnfinishedRound_KeepsTheRoundsStart_AndWhatWasAnsweredInIt()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var since = (await WallAsync(h, w.WallId)).LookOverPendingSince;
        await service.SettleUnsureMatchesAsync(w.WallId);

        await ApplyAgainAsync(await StageAgainAsync(h, w), score: 0.4);

        var state = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((since, 1, 2), (state.PendingSince, state.OpenFindings, state.TotalFindings));
        Assert.Equal([LookOverFindingStatus.Open, LookOverFindingStatus.Settled], (await service.ListAsync(w.WallId)).Select(f => f.Status));
    }

    [Fact]
    public async Task ALaterUpdateThatLeavesNothing_SupersedesWhatWasOpen_ButTheWallStaysFlaggedUntilFinish()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);

        await ApplyAgainAsync(await StageAgainAsync(h, w), score: 0.9);

        Assert.Equal(LookOverFindingStatus.Superseded, (await FindingAsync(h, w.WallId)).Status);
        var state = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((true, 0, 0), (state.Pending, state.OpenFindings, state.TotalFindings));
        Assert.Empty(await service.ListAsync(w.WallId));

        await service.FinishAsync(w.WallId, leaveOpen: false);
        Assert.False((await service.GetStateAsync(w.WallId))!.Pending);
    }

    [Fact]
    public async Task WhenTheFindingsCannotBeWorkedOut_ThePromoteStillCommits_AndTheOldFindingsOfThePanelAreSuperseded()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);
        Guid livePanel;
        await using (var db = h.CreateContext())
        {
            livePanel = (await db.WallPanels.SingleAsync(p => p.WallId == w.WallId && p.Photo != null)).Id;
        }

        var earlier = await AddFindingAsync(
            h, new WallLookOverFinding { WallId = w.WallId, Generation = 2, Kind = LookOverFindingKind.NotFound, HoldId = w.TargetId, PanelId = livePanel });
        w.Service.LookOverBuilder = _ => throw new InvalidOperationException("The builder is broken.");
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);

        await PromoteLikeApplyAsync(w, session);

        var wall = await WallAsync(h, w.WallId);
        Assert.Equal((3, (DateTimeOffset?)null), (wall.CurrentGeneration, wall.LookOverPendingSince));
        await using var after = h.CreateContext();
        var finding = await after.WallLookOverFindings.SingleAsync();
        Assert.Equal((earlier, LookOverFindingStatus.Superseded), (finding.Id, finding.Status));
        Assert.Equal(WallUpdateSessionStatus.Promoted, (await after.WallUpdateSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task AFindingThatPointsAtAHoldOfAnEarlierGeneration_CanOnlyBeAcknowledged_AndNoAnswerTouchesThatHold()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.9);
        var service = Service(h);
        Guid livePanel;
        Guid liveHold;
        await using (var db = h.CreateContext())
        {
            livePanel = (await db.WallPanels.SingleAsync(p => p.WallId == w.WallId && p.Generation == 3)).Id;
            liveHold = (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.BoulderId)).HoldId;
        }

        // As if retiring it had failed: still open, about the retained row of the generation before, with a live hold beside it.
        var stale = await AddFindingAsync(h, new WallLookOverFinding
        {
            WallId = w.WallId, Generation = 2, Kind = LookOverFindingKind.NotFound, HoldId = w.TargetId, OtherHoldId = liveHold, PanelId = livePanel,
        });

        var view = Assert.Single(await service.ListAsync(w.WallId));

        Assert.Equal(((Guid?)null, 0), (view.HoldId, view.Boulders.Count));
        Assert.Equal([LookOverAnswer.Settle], view.Answers);
        foreach (var answer in new[] { LookOverAnswer.RemoveHold, LookOverAnswer.UseNewHold, LookOverAnswer.DifferentHold })
        {
            await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, stale, answer, []));
        }

        Assert.NotNull(await HoldAsync(h, w.TargetId));
        Assert.NotNull(await HoldAsync(h, liveHold));
        await service.AnswerAsync(w.WallId, stale, LookOverAnswer.Settle);
        Assert.NotNull(await HoldAsync(h, w.TargetId));
    }

    [Fact]
    public async Task AHoldThatWasAlreadyMarkedBeforeTheUpdate_KeepsItsMark_WhenTheFindingIsSettled()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.4);
        await using (var db = h.CreateContext())
        {
            await db.Holds.Where(x => x.Id == w.TargetId).ExecuteUpdateAsync(s => s.SetProperty(x => x.NeedsReview, true));
        }

        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        await PromoteLikeApplyAsync(w, session);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.False((await FindingAsync(h, w.WallId)).FlaggedHold);

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        // The card asked too, but the mark was there before it: somebody else's to clear.
        Assert.True((await HoldAsync(h, w.DetectionId))!.NeedsReview);
        Assert.Equal(LookOverFindingStatus.Settled, (await FindingAsync(h, w.WallId)).Status);
    }

    [Fact]
    public async Task AHoldNotFoundAgain_KeepsWhateverMarkItHas_WhenItIsSettledAsStillThere()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        await using (var db = h.CreateContext())
        {
            await db.Holds.Where(x => x.Id == view.HoldId).ExecuteUpdateAsync(s => s.SetProperty(x => x.NeedsReview, true));
        }

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.True((await HoldAsync(h, view.HoldId))!.NeedsReview);
    }
}
