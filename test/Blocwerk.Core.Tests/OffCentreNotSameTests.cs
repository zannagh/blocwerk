// <copyright file="OffCentreNotSameTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Not the same hold" on an unsure-match card about a hold on a panel OTHER than the centre, applied the way the quick
/// flow's Apply does (<see cref="RefreshDecisions.LoadAsync"/> and its scope reconcile). The card showed the pair, so the
/// answer sticks through Apply exactly as on the centre: the old hold is carried blind, unconfirmed and marked, the
/// detection goes live as a new hold, and the look-over asks about the old hold. It used to be reset to the matcher's
/// twin at the promote, which left neither the answer nor a finding.
/// </summary>
public class OffCentreNotSameTests
{
    private const double SideX = 0.4;
    private const double SideY = 0.65;

    [Fact]
    public async Task NotTheSameHold_OnTheRightPanel_SticksThroughApply_AndLeavesAFinding()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true, neighbour: new LookNeighbour(SideX, SideY, OldHold: true));
        w.Probe.Set(SideX, SideY, 0.4);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        var oldId = w.NeighbourTargetId!.Value;
        var detectionId = w.NeighbourDetectionId!.Value;
        var card = (await w.Sessions.GetUpdateExceptionsAsync(w.WallId))
            .Single(c => c.Kind == UpdateExceptionKind.LowConfidenceMatch && c.OldHoldId == oldId);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, UpdateExceptionAnswer.NotSame, w.SessionId);

        var actors = new WallRefreshActors(null!, null!, w.Service, w.Sessions, null);
        var promotable = await RefreshDecisions.LoadAsync(w.WallId, actors, session, w.StagedByPanel);
        Assert.Equal(0, promotable.Resets);
        var decision = promotable.Scoped.Carryover.Single(d => d.OldHoldId == oldId);
        Assert.Equal((CarryKind.Carried, (Guid?)null, false), (decision.Kind, decision.NewHoldId, decision.Confirmed));

        await w.Service.PromoteAsync(w.WallId, promotable.Scoped with { ExpectedMovesVersion = promotable.Moves.Version }, w.SessionId);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == oldId);
        Assert.NotEqual(detectionId, link.NewHoldId);
        var successor = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        Assert.Equal((3, true), (successor.Generation, successor.NeedsReview));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == detectionId && x.Generation == 3));
        Assert.True(await db.WallLookOverFindings.AnyAsync(f => f.OldHoldId == oldId));
    }
}
