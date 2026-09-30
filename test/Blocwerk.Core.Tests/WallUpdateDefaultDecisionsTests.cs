// <copyright file="WallUpdateDefaultDecisionsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The quick review's default decisions are written in one go (carryover, new holds, neighbour panels, phase), and not at
/// all when the session was changed after the given moment: a person's choices are never overwritten by defaults.
/// </summary>
public class WallUpdateDefaultDecisionsTests
{
    [Fact]
    public async Task Save_WritesEveryPartAndThePhase()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin, neighbour, detection) = await OpenSessionAsync(h);

        var written = await sessions.SaveDefaultDecisionsAsync(h.WallId, Decisions(old.Id, twin, neighbour, detection));

        Assert.True(written);
        var read = await sessions.GetDecisionsAsync(h.WallId);
        Assert.Equal(twin, Assert.Single(read.Carryover).NewHoldId);
        Assert.Equal([detection], Assert.Single(read.Neighbours).RemovedNeighbourHoldIds);
        Assert.Equal(WallUpdatePhase.Carryover, (await sessions.GetOpenSessionAsync(h.WallId))!.Phase);
    }

    [Fact]
    public async Task Save_AfterAPersonChangedTheSession_WritesNothing()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin, neighbour, detection) = await OpenSessionAsync(h);
        var since = DateTimeOffset.UtcNow;
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Removed, null));

        var written = await sessions.SaveDefaultDecisionsAsync(h.WallId, Decisions(old.Id, twin, neighbour, detection), since);

        Assert.False(written);
        var read = await sessions.GetDecisionsAsync(h.WallId);
        Assert.Equal(CarryKind.Removed, Assert.Single(read.Carryover).Kind);
        Assert.Empty(read.Neighbours);
    }

    private static DefaultDecisions Decisions(Guid old, Guid twin, Guid neighbour, Guid detection) =>
        new(
            [new CarryoverDecision(old, CarryKind.Carried, twin)],
            [],
            [],
            [new NeighbourLinkSet(neighbour, [], [detection])],
            WallUpdatePhase.Carryover);

    private static async Task<(WallUpdateSessionService Sessions, Guid Twin, Guid Neighbour, Guid Detection)> OpenSessionAsync(WallTestHarness h)
    {
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());
        var centre = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var neighbour = await WallUpdateSessionFixture.PanelIdAsync(h, 1, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centre, 1);
        var detection = await WallUpdateSessionFixture.AddStagedHoldAsync(h, neighbour, 1);
        await using (var db = h.CreateContext())
        {
            var session = await db.WallUpdateSessions.SingleAsync(s => s.WallId == h.WallId && s.Status == WallUpdateSessionStatus.Open);
            session.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        return (WallUpdateSessionFixture.Sessions(h), twin, neighbour, detection);
    }
}
