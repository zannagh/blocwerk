// <copyright file="LookOverAnswerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The answers after the update is live, each with what it does to the hold and to the boulder that uses it. Only a
/// person's answer ever removes a hold or moves a boulder onto another one; settling changes nothing on the wall.
/// </summary>
public class LookOverAnswerTests
{
    [Fact]
    public async Task AReplacedHold_OffersKeepUseTheNewHoldAndGone_AndListsItsBoulder()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);

        var view = Assert.Single(await Service(h).ListAsync(w.WallId));

        Assert.Equal((LookOverFindingKind.ReplacedByOther, "Yellow crimp", true, w.DetectionId), (view.Kind, view.HoldName, view.HasOld, view.OtherHoldId));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.UseNewHold, LookOverAnswer.RemoveHold], view.Answers);
        var boulder = Assert.Single(view.Boulders);
        Assert.Equal((w.BoulderId, "Crimp line", true), (boulder.Id, boulder.Name, boulder.NeedsReview));
    }

    [Fact]
    public async Task KeepOldHoldHere_SettlesTheFinding_ClearsTheHoldsMark_AndLeavesTheBoulderMarked()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal(
            (LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.Settle, (Guid?)h.Owner.Id),
            (finding.Status, finding.Answer, finding.AnsweredByUserId));
        Assert.False((await HoldAsync(h, view.HoldId))!.NeedsReview);
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((true, false, view.HoldId), (boulder.NeedsReview, boulder.IsHistoric, (Guid?)boulder.BoulderHolds.Single().HoldId));
        Assert.NotNull(await HoldAsync(h, w.DetectionId));

        // Settled, not finished: the wall stays flagged until somebody finishes the look-over.
        Assert.NotNull((await WallAsync(h, w.WallId)).LookOverPendingSince);
        Assert.Empty(Assert.Single(await service.ListAsync(w.WallId)).Answers);
    }

    [Fact]
    public async Task OldHoldIsGone_DeletesTheHold_AndItsBoulderBecomesHistoric()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, Shown(view));

        Assert.Null(await HoldAsync(h, view.HoldId));
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((true, 0), (boulder.IsHistoric, boulder.BoulderHolds.Count));
        Assert.NotNull(await HoldAsync(h, w.DetectionId));
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.RemoveHold), await StatusAsync(h, w.WallId));
        var after = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal(((Guid?)null, 0), (after.HoldId, after.Boulders.Count));
    }

    [Fact]
    public async Task BouldersShouldUseTheNewHold_MergesTheOldHoldIntoIt_AndTheBoulderStaysCurrentAndMarked()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, Shown(view));

        Assert.Null(await HoldAsync(h, view.HoldId));
        var boulder = await BoulderAsync(h, w.BoulderId);
        var membership = Assert.Single(boulder.BoulderHolds);
        Assert.Equal((w.DetectionId, HoldType.Start, false, true), ((Guid?)membership.HoldId, membership.Type, boulder.IsHistoric, boulder.NeedsReview));
        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.UseNewHold), (finding.Status, finding.Answer));
        await using var db = h.CreateContext();
        Assert.True(await db.ChangeJournalBatches.AnyAsync(b => b.Id == finding.MergeBatchId && b.Label == HoldDuplicateService.BatchLabel));
    }

    [Fact]
    public async Task SameHold_SettlesAnUnsureMatch_AndTakesTheMarkOffTheHold()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.DifferentHold], view.Answers);
        Assert.True((await HoldAsync(h, w.DetectionId))!.NeedsReview);

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.False((await HoldAsync(h, w.DetectionId))!.NeedsReview);
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((false, false, w.DetectionId), (boulder.NeedsReview, boulder.IsHistoric, (Guid?)boulder.BoulderHolds.Single().HoldId));
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.Settle), await StatusAsync(h, w.WallId));
    }

    [Fact]
    public async Task DifferentHold_MarksTheHoldChangedAndItsBoulderForReview_AndAsksOnAsAReplacedHold()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.DifferentHold, Shown(view));

        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((true, false, w.DetectionId), (boulder.NeedsReview, boulder.IsHistoric, (Guid?)boulder.BoulderHolds.Single().HoldId));
        Assert.True((await HoldAsync(h, w.DetectionId))!.NeedsReview);
        var next = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((LookOverFindingKind.ReplacedByOther, LookOverFindingStatus.Open, true), (next.Kind, next.Status, next.Converted));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.RemoveHold], next.Answers);

        // Keeping it afterwards settles the finding and leaves the "changed" mark a person just set.
        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);
        Assert.True((await HoldAsync(h, w.DetectionId))!.NeedsReview);
    }

    [Fact]
    public async Task StillThere_SettlesAHoldThatWasNotFoundAgain_AndGoneIsOfferedToo()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.RemoveHold], view.Answers);

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        var boulder = await BoulderAsync(h, w.BoulderId);

        // The boulder was only marked because this hold was not found: it is cleared (LookOverBoulderClearingTests has the rest).
        Assert.Equal((false, false, view.HoldId), (boulder.NeedsReview, boulder.IsHistoric, (Guid?)boulder.BoulderHolds.Single().HoldId));
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.Settle), await StatusAsync(h, w.WallId));
    }

    [Fact]
    public async Task AnAnswerThatDoesNotFit_OrASecondAnswer_IsRefused_AndChangesNothing()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, Shown(view)));
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, Shown(view)));
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, Guid.NewGuid(), LookOverAnswer.Settle));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AnswerAsync(Guid.NewGuid(), view.Id, LookOverAnswer.Settle));
        Assert.NotNull(await HoldAsync(h, w.DetectionId));
        Assert.Equal((LookOverFindingStatus.Open, (LookOverAnswer?)null), await StatusAsync(h, w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle));
    }

    [Fact]
    public async Task AFindingWhoseHoldWasDeletedInTheEditor_IsClosed_AndTakesNoAnswer()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        await h.WallService.DeleteHoldAsync(view.HoldId!.Value);

        var after = Assert.Single(await service.ListAsync(w.WallId));

        Assert.Equal((LookOverFindingStatus.Closed, (LookOverCloseReason?)LookOverCloseReason.RemovedInEditor), (after.Status, after.ClosedReason));
        Assert.Empty(after.Answers);
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle));
        Assert.Equal(0, (await service.GetStateAsync(w.WallId))!.OpenFindings);
    }

    private static Guid[] Shown(LookOverFindingView view) => [.. view.Boulders.Select(b => b.Id)];

    private static async Task<(LookOverFindingStatus Status, LookOverAnswer? Answer)> StatusAsync(WallTestHarness h, Guid wallId)
    {
        var finding = await FindingAsync(h, wallId);
        return (finding.Status, finding.Answer);
    }
}
