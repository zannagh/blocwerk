// <copyright file="PostgresLookOverTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
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

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold);
        await service.FinishAsync(w.WallId, leaveOpen: false);

        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((w.DetectionId, false, true), ((Guid?)boulder.BoulderHolds.Single().HoldId, boulder.IsHistoric, boulder.NeedsReview));
        Assert.Null(await HoldAsync(h, view.HoldId));
        var after = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((false, 0, "Owner"), (after.Pending, after.OpenFindings, after.CompletedBy));
        Assert.NotNull((await FindingAsync(h, w.WallId)).MergeBatchId);
    }

    [PostgresFact]
    public async Task RemovingTheHoldOfAFinding_LeavesTheFindingBehind_Settled()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold);

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingStatus.Settled, view.HoldId), (finding.Status, finding.HoldId));
        Assert.True((await BoulderAsync(h, w.BoulderId)).IsHistoric);
        Assert.Equal([], Assert.Single(await service.ListAsync(w.WallId)).Answers);
    }
}
