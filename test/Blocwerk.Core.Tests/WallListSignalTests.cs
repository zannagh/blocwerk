// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall list and the kept "no access" answers follow the commits (PR 1.8), replacing the DomainChange WallList
/// scope: a join or a new wall (<c>walls.changed</c>, addressed to that user), a removal (<c>access.revoked</c>), a
/// deleted wall and a change of a listed wall all reach the circuit's list before the save returns.
/// </summary>
public sealed class WallListSignalTests
{
    [Fact]
    public async Task JoiningAWall_ListsItAtOnce_AndForgetsTheNoAccessAnswer()
    {
        await using var f = new OwnWriteFixture();
        await f.StartAsync();
        var token = await f.Circuit.Walls.GetOrCreateShareTokenAsync(f.WallId);
        var joiner = await AddUserAsync(f, "joiner@test");
        f.Harness.ActingUser = joiner;
        f.Viewer.Viewer = null;
        Assert.Empty(await f.Cache.GetMyWallsAsync());
        Assert.Null(await f.Link.EnsureAsync(f.WallId)); // kept: a non-member is not re-read on every miss
        var resolved = f.Viewer.Resolved;

        await f.Request().Walls.JoinWallAsync(token);

        Assert.Equal(f.WallId, Assert.Single(await f.Cache.GetMyWallsAsync()).Id);
        Assert.Contains(f.Changes, c => c == WallCacheChange.WallList);
        Assert.Contains(f.Recorded.Rows, r => r.Kind == FeedKinds.WallsChanged && r.AudienceUserId == joiner.Id && r.WallId == f.WallId);
        f.Viewer.Viewer = FeedViewer.Member(joiner.Id);
        Assert.NotNull(await f.Link.EnsureAsync(f.WallId));
        Assert.Equal(resolved + 1, f.Viewer.Resolved);
    }

    [Fact]
    public async Task CreatingAndDeletingAWall_ShowInTheListAtOnce()
    {
        await using var f = new OwnWriteFixture();
        await f.StartAsync();
        Assert.Single(await f.Cache.GetMyWallsAsync());

        var created = await f.Circuit.Walls.CreateWallAsync("Second wall", null);
        Assert.Contains(await f.Cache.GetMyWallsAsync(), w => w.Id == created.Id);

        await f.Circuit.Walls.DeleteWallAsync(created.Id);
        Assert.DoesNotContain(await f.Cache.GetMyWallsAsync(), w => w.Id == created.Id);
    }

    [Fact]
    public async Task ARemovalOrARenameOfAListedWall_ReloadsTheList()
    {
        await using var f = new OwnWriteFixture();
        await f.StartAsync();
        var member = await f.Harness.AddMemberAsync("member@test", WallRole.Member);
        Assert.Equal(2, Assert.Single(await f.Cache.GetMyWallsAsync()).Members.Count);

        await using (var db = TestDb.Create(f.Harness.DbContextFactory.ConnectionString, f.Interceptor))
        {
            db.WallMembers.Remove(await db.WallMembers.SingleAsync(m => m.UserId == member.Id));
            await db.SaveChangesAsync();
        }

        Assert.Single(Assert.Single(await f.Cache.GetMyWallsAsync()).Members);

        await f.Circuit.Walls.UpdateWallAsync(f.WallId, "Renamed", null);
        Assert.Equal("Renamed", Assert.Single(await f.Cache.GetMyWallsAsync()).Name);
    }

    [Fact]
    public async Task AChangeOfAnUnlistedWall_OrOfAListedWallsHolds_KeepsTheList()
    {
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        var list = f.Cache.GetMyWallsAsync();
        await list;

        await using (var db = TestDb.Create(f.Harness.DbContextFactory.ConnectionString, f.Interceptor))
        {
            var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
            db.Users.Add(stranger);
            db.Walls.Add(new Wall { Name = "Someone else's", OwnerId = stranger.Id });
            await db.SaveChangesAsync();
        }

        await f.Circuit.Walls.UpdateHoldAsync(holds[0].Id, new Services.HoldEdit { X = 0.3, Y = 0.3, Radius = 0.02 });

        Assert.Same(list, f.Cache.GetMyWallsAsync());
        Assert.DoesNotContain(f.Changes, c => c == WallCacheChange.WallList);
    }

    private static async Task<User> AddUserAsync(OwnWriteFixture f, string identifier)
    {
        var user = new User { Identifier = identifier, DisplayName = identifier };
        await using var db = f.Harness.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
