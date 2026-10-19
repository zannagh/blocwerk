// <copyright file="WallUpdateSessionScopedWriteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Decision writes and staged-hold edits name the update session the tab is looking at (review C2): after another
/// admin took the wall over, a write from the stale tab is refused instead of landing in the new update, whose old-hold
/// ids are the same. Without a session id the writes still go to whatever is open (legacy callers).
/// </summary>
public class WallUpdateSessionScopedWriteTests
{
    [Fact]
    public async Task ACarryVerdict_FromAStaleTab_IsRefused_AndNotWrittenIntoTheNewUpdate()
    {
        using var h = new WallTestHarness();
        var (old, stale, current) = await TakenOverAsync(h);
        var sessions = WallUpdateSessionFixture.Sessions(h);

        h.ActingUser = h.Owner;
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old, CarryKind.Removed, null, Confirmed: true), stale));

        await using var db = h.CreateContext();
        Assert.False(await db.WallUpdateHoldDecisions.AnyAsync(d => d.SessionId == current && d.HoldId == old));
    }

    [Fact]
    public async Task EveryDecisionWrite_FromAStaleTab_IsRefused()
    {
        using var h = new WallTestHarness();
        var (old, stale, current) = await TakenOverAsync(h);
        var sessions = WallUpdateSessionFixture.Sessions(h);
        var centre = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        h.ActingUser = h.Owner;

        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() => sessions.SetPhaseAsync(h.WallId, WallUpdatePhase.Confirm, 0, stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() => sessions.ClearCarryConfirmationAsync(h.WallId, old, stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() => sessions.SaveNewCentreHoldDecisionAsync(h.WallId, old, true, stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            sessions.SaveCarryOutcomeAsync(h.WallId, [new CarryoverDecision(old, CarryKind.Removed, null)], [], [], stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            sessions.SaveNeighbourLinkSetAsync(h.WallId, new NeighbourLinkSet(centre, [], [old]), stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            sessions.DecideUpdateExceptionAsync(h.WallId, Guid.NewGuid(), UpdateExceptionAnswer.Remove, stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            sessions.DecideRelocationAsync(h.WallId, Guid.NewGuid(), RelocationDecision.Moved, stale));

        await using var db = h.CreateContext();
        var session = await db.WallUpdateSessions.SingleAsync(s => s.Id == current);
        Assert.Equal(WallUpdatePhase.Detected, session.Phase);
        Assert.False(await db.WallUpdateHoldDecisions.AnyAsync(d => d.SessionId == current));
        Assert.False(await db.WallUpdateNeighbourDecisions.AnyAsync(d => d.SessionId == current));
    }

    [Fact]
    public async Task Writes_NamingTheOpenSession_OrNone_AreTaken()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var sessions = WallUpdateSessionFixture.Sessions(h);
        var mine = (await sessions.GetOpenSessionAsync(h.WallId))!.Id;

        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Removed, null), mine);
        var moved = await sessions.SetPhaseAsync(h.WallId, WallUpdatePhase.Neighbours, 0, mine);
        await sessions.SetPhaseAsync(h.WallId, WallUpdatePhase.Touchup);

        Assert.Equal(WallUpdatePhase.Neighbours, moved.Phase);
        var decisions = await sessions.GetDecisionsAsync(h.WallId);
        Assert.Equal(CarryKind.Removed, Assert.Single(decisions.Carryover).Kind);
    }

    [Fact]
    public async Task StagedHoldEdits_FromAStaleTab_AreRefused_AndTouchNothing()
    {
        using var h = new WallTestHarness();
        var (_, stale, _) = await TakenOverAsync(h);
        var centre = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var hold = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centre, stagedGen: 1, x: 0.4, y: 0.4);
        var panels = WallUpdateSessionFixture.Panels(h);
        h.ActingUser = h.Owner;

        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            panels.AddStagedHoldInSessionAsync(h.WallId, stale, centre, 0.2, 0.2, 0.02));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            panels.UpdateStagedHoldInSessionAsync(h.WallId, stale, hold, 0.9, 0.9, 0.02));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            panels.DeleteStagedHoldInSessionAsync(h.WallId, stale, hold));

        await using var db = h.CreateContext();
        var row = await db.Holds.SingleAsync(x => x.Id == hold);
        Assert.Equal((0.4, 0.4), (row.X, row.Y));
        Assert.Equal(1, await db.Holds.CountAsync(x => x.Generation == 1));
    }

    /// <summary>
    /// The owner staged an update (the stale session), then a helper took the wall over (the current one). Returns the
    /// old live hold, which both updates share.
    /// </summary>
    private static async Task<(Guid OldHoldId, Guid Stale, Guid Current)> TakenOverAsync(WallTestHarness h)
    {
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        var service = WallUpdateSessionFixture.BigUpdate(h);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var stale = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!.Id;

        h.ActingUser = await h.AddMemberAsync("helper@test", WallRole.Admin);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto(), takeOverExisting: true);
        var current = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!.Id;
        return (old.Id, stale, current);
    }
}
