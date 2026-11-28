// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The training and session writes' optional <c>clientRequestId</c>: the sender's own replay returns the stored row,
/// another user's same id is their own new row, the empty id is refused, and a replayed session start ends nothing.
/// </summary>
public sealed class TrainingReplayKeyTests
{
    [Fact]
    public async Task HangboardAndPullups_ReplayOnceForTheSender_AndNotForAnyoneElse()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var training = new TrainingService(h.DbContextFactory, h.CurrentUser, NullLogger<TrainingService>.Instance);
        var key = Guid.NewGuid();

        var hang = await training.SaveHangboardSessionAsync(20, 0, TimeSpan.FromSeconds(10), 3, clientRequestId: key);
        var pull = await training.SavePullupSessionAsync(5, 0, 3, clientRequestId: key);
        Assert.Equal(hang.Id, (await training.SaveHangboardSessionAsync(20, 0, TimeSpan.FromSeconds(10), 3, clientRequestId: key)).Id);
        Assert.Equal(pull.Id, (await training.SavePullupSessionAsync(5, 0, 3, clientRequestId: key)).Id);

        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Assert.NotEqual(hang.Id, (await training.SaveHangboardSessionAsync(20, 0, TimeSpan.FromSeconds(10), 3, clientRequestId: key)).Id);
        Assert.NotEqual(pull.Id, (await training.SavePullupSessionAsync(5, 0, 3, clientRequestId: key)).Id);

        await using var db = h.CreateContext();
        Assert.Equal(2, await db.HangboardSessions.CountAsync());
        Assert.Equal(2, await db.PullupSessions.CountAsync());
    }

    [Fact]
    public async Task AReplayedSessionStart_ReturnsTheSessionAndEndsNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var key = Guid.NewGuid();

        var started = await h.SessionService.StartSessionAsync(h.WallId, key);
        var replayed = await h.SessionService.StartSessionAsync(h.WallId, key);

        Assert.Equal(started.Id, replayed.Id);
        Assert.Equal("Test Wall", replayed.Wall.Name);
        var active = await h.SessionService.GetActiveSessionAsync();
        Assert.Equal(started.Id, active?.Id);
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.ClimbingSessions.CountAsync());
    }

    [Fact]
    public async Task ASessionKeyReusedOnAnotherWall_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var otherWall = Guid.NewGuid();
        await using (var db = h.CreateContext())
        {
            db.Walls.Add(new Entities.Wall { Id = otherWall, Name = "Other", OwnerId = h.Owner.Id });
            db.WallMembers.Add(new Entities.WallMember { WallId = otherWall, UserId = h.Owner.Id, Role = WallRole.Admin });
            await db.SaveChangesAsync();
        }

        var key = Guid.NewGuid();
        await h.SessionService.StartSessionAsync(h.WallId, key);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.SessionService.StartSessionAsync(otherWall, key));
        Assert.NotNull(await h.SessionService.GetActiveSessionAsync());
    }

    [Fact]
    public async Task TheEmptyId_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var training = new TrainingService(h.DbContextFactory, h.CurrentUser, NullLogger<TrainingService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => training.SaveHangboardSessionAsync(20, 0, TimeSpan.FromSeconds(10), 3, clientRequestId: Guid.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => training.SavePullupSessionAsync(5, 0, 3, clientRequestId: Guid.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.SessionService.StartSessionAsync(h.WallId, Guid.Empty));
    }
}
