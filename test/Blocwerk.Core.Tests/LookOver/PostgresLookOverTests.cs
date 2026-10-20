// <copyright file="PostgresLookOverTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The look-over on a real PostgreSQL: the migration's table and wall columns, the finding written inside the promote's
/// transaction, the merge behind "boulders should use the new hold" with its journal batch, and finishing.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresLookOverTests
{
    [PostgresFact]
    public async Task AnAppliedUpdate_FlagsTheWall_AndItsFindingIsAnsweredAndFinished()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);

        var state = (await service.GetStateAsync(w.WallId))!;
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((true, 1, LookOverFindingKind.ReplacedByOther, w.DetectionId), (state.Pending, state.OpenFindings, view.Kind, view.OtherHoldId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [.. view.Boulders.Select(b => b.Id)]);
        await service.FinishAsync(w.WallId, leaveOpen: false);

        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((w.DetectionId, false, false), (boulder.BoulderHolds.Single().HoldId, boulder.IsHistoric, boulder.NeedsReview));
        Assert.Null(await HoldAsync(h, view.HoldId));
        var after = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((false, 0, "Owner"), (after.Pending, after.OpenFindings, after.CompletedBy));
        Assert.NotNull((await FindingAsync(h, w.WallId)).MergeBatchId);
    }

    [PostgresFact]
    public async Task TwoMissingHoldsOfOneBoulder_AreClearedByTheLastAnswer_AndUndoMarksTheBoulderAgain()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var (w, _) = await ApplyTwoMissingAsync(h);
        var service = Service(h);
        var listed = await service.ListAsync(w.WallId);

        // The uuid[] columns round-trip: what the promote recorded, and what an answer cleared.
        Assert.Equal([w.BoulderId], (await FindingsAsync(h, w.WallId)).Select(f => f.MarkedBoulderIds!.Single()).Distinct());
        await service.AnswerAsync(w.WallId, listed[0].Id, LookOverAnswer.Settle);
        await service.AnswerAsync(w.WallId, listed[1].Id, LookOverAnswer.Settle);
        Assert.False((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.Equal([w.BoulderId], (await FindingsAsync(h, w.WallId)).Single(f => f.Id == listed[1].Id).ClearedBoulderIds);

        await service.UndoAsync(w.WallId, listed[0].Id);

        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);
        Assert.Equal(LookOverFindingStatus.Open, (await FindingsAsync(h, w.WallId)).Single(f => f.Id == listed[0].Id).Status);
    }

    [PostgresFact]
    public async Task RemovingTheHoldOfAFinding_LeavesTheFindingBehind_Settled()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [.. view.Boulders.Select(b => b.Id)]);

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingStatus.Settled, view.HoldId), (finding.Status, finding.HoldId));
        Assert.True((await BoulderAsync(h, w.BoulderId)).IsHistoric);
        Assert.Equal([], Assert.Single(await service.ListAsync(w.WallId)).Answers);
    }

    [PostgresFact]
    public async Task HoldsTheUpdateCouldNotFind_AreMarked_BackfilledForAnEarlierApply_AndRemovedAtOnce()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var k = await KeptHoldFixture.ApplyAsync(h, covered: true);
        var service = Service(h);
        Assert.Equal(2, (await service.GetStateAsync(k.Wall.WallId))!.MissingHolds);

        await KeptHoldFixture.ForgetKeptStateAsync(h, k);
        Assert.Equal(0, (await service.GetStateAsync(k.Wall.WallId))!.MissingHolds);
        await KeptHoldBackfill.RunIfNeededAsync(h.DbContextFactory, null, NullLogger.Instance);

        var views = await service.ListAsync(k.Wall.WallId);
        Assert.Equal([true, true], views.Select(v => v.Missing));
        var removed = await service.RemoveUnusedAsync(k.Wall.WallId, [.. views.Select(v => v.Id)], onlyCovered: true);
        Assert.Equal(new LookOverBulkResult(1, 2), removed);
        Assert.Null(await HoldAsync(h, k.UnusedId));
        Assert.Equal(1, (await service.GetStateAsync(k.Wall.WallId))!.MissingHolds);
    }

    [PostgresFact]
    public async Task AHoldMergedInTheDuplicateReview_ClosesItsFindingAsMerged()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var k = await KeptHoldFixture.ApplyAsync(h);

        await Duplicates(h).MergeAsync(k.Wall.WallId, k.FoundId, k.UnusedId, HoldMergeMode.KeepLeft);

        var view = (await Service(h).ListAsync(k.Wall.WallId)).Single(v => v.OldHoldId == k.UnusedOldId);
        Assert.Equal((LookOverFindingStatus.Closed, (LookOverCloseReason?)LookOverCloseReason.MergedInEditor), (view.Status, view.ClosedReason));
    }
}
