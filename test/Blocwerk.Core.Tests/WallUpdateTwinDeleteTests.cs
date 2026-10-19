// <copyright file="WallUpdateTwinDeleteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Deleting a staged hold that an old hold's verdict names as its twin (review C1). The twin is a cascading foreign
/// key, so the plain delete took the old hold's "Changed" with it and the promote carried the hold unflagged. The
/// update's delete now refuses unless told what to do, and "release" keeps the verdict without the twin.
/// </summary>
public class WallUpdateTwinDeleteTests
{
    [Fact]
    public async Task DeletingAMatchedTwin_IsRefused_AndTheChangedVerdictStays()
    {
        using var h = new WallTestHarness();
        var (old, twin, session) = await ChangedOntoTwinAsync(h);
        var panels = WallUpdateSessionFixture.Panels(h);

        var refused = await Assert.ThrowsAsync<StagedHoldIsTwinException>(() =>
            panels.DeleteStagedHoldInSessionAsync(h.WallId, session, twin));

        Assert.Equal([old], refused.OldHoldIds);
        await using var db = h.CreateContext();
        Assert.True(await db.Holds.AnyAsync(x => x.Id == twin));
        var row = await db.WallUpdateHoldDecisions.SingleAsync(d => d.HoldId == old);
        Assert.Equal((CarryKind.Changed, (Guid?)twin, true), (row.CarryKind, row.PairedHoldId, row.Confirmed));
    }

    [Fact]
    public async Task ReleasingTheTwin_KeepsTheVerdict_WithoutTheMatch_Unreviewed()
    {
        using var h = new WallTestHarness();
        var (old, twin, session) = await ChangedOntoTwinAsync(h);

        var released = await WallUpdateSessionFixture.Panels(h).DeleteStagedHoldInSessionAsync(h.WallId, session, twin, StagedTwinDeletion.ReleaseTwin);

        Assert.Equal(1, released);
        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == twin));
        var row = await db.WallUpdateHoldDecisions.SingleAsync(d => d.HoldId == old);
        Assert.Equal((CarryKind.Changed, (Guid?)null, false), (row.CarryKind, row.PairedHoldId, row.Confirmed));
        var decisions = await WallUpdateSessionFixture.Sessions(h).GetDecisionsAsync(h.WallId);
        Assert.Equal(CarryKind.Changed, Assert.Single(decisions.Carryover).Kind);
    }

    [Fact]
    public async Task AHoldNoVerdictNames_IsDeleted_WithoutAsking()
    {
        using var h = new WallTestHarness();
        var (_, _, session) = await ChangedOntoTwinAsync(h);
        var centre = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var stray = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centre, stagedGen: 1, x: 0.8, y: 0.8);

        await WallUpdateSessionFixture.Panels(h).DeleteStagedHoldInSessionAsync(h.WallId, session, stray);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == stray));
        Assert.Equal(1, await db.WallUpdateHoldDecisions.CountAsync());
    }

    /// <summary>An update whose one old hold is confirmed "Changed" onto a staged twin.</summary>
    private static async Task<(Guid Old, Guid Twin, Guid Session)> ChangedOntoTwinAsync(WallTestHarness h)
    {
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var sessions = WallUpdateSessionFixture.Sessions(h);
        var session = (await sessions.GetOpenSessionAsync(h.WallId))!.Id;
        var centre = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centre, stagedGen: 1);
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Changed, twin, Confirmed: true), session);
        return (old.Id, twin, session);
    }
}
