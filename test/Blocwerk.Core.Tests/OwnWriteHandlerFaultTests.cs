// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.State;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The PR 1.8 review's probe (W1): a <see cref="WallCacheState.Changed"/> handler that throws must not stop the rest of a
/// commit for that circuit: the later walls' evictions, the wall-list forget, the denied answers, and the other handlers.
/// </summary>
public sealed class OwnWriteHandlerFaultTests
{
    [Fact]
    public async Task AThrowingHandler_StopsNothingElseOfTheCommit()
    {
        await using var f = new OwnWriteFixture();
        await f.StartAsync();
        var second = await f.Circuit.Walls.CreateWallAsync("Second", null);
        await f.Cache.GetWallAsync(f.WallId);
        await f.Cache.GetWallAsync(second.Id);
        await f.Cache.GetMyWallsAsync();
        f.Viewer.Viewer = null;
        var denied = Guid.NewGuid();
        Assert.Null(await f.Link.EnsureAsync(denied));
        var resolved = f.Viewer.Resolved;

        var heard = new List<WallCacheChange>();
        f.Cache.Changed += _ => throw new InvalidOperationException("a disposed page");
        f.Cache.Changed += c => heard.Add(c);

        f.Recorded.Commits.Publish([
            Row(f.WallId, FeedKinds.HoldChanged, FeedAudience.Wall),
            Row(second.Id, FeedKinds.HoldChanged, FeedAudience.Wall),
            Row(denied, FeedKinds.WallsChanged, FeedAudience.User),
        ]);

        Assert.False(f.Cache.Store.Walls.TryPeek(f.WallId, out _));
        Assert.False(f.Cache.Store.Walls.TryPeek(second.Id, out _));
        Assert.Contains(WallCacheChange.OfWall(f.WallId), heard);
        Assert.Contains(WallCacheChange.OfWall(second.Id), heard);
        Assert.Contains(WallCacheChange.WallList, heard);
        Assert.Null(await f.Link.EnsureAsync(denied));
        Assert.Equal(resolved + 1, f.Viewer.Resolved); // the denied answer was forgotten and read again
    }

    private static WallChange Row(Guid wallId, string kind, FeedAudience audience) => new()
    {
        WallId = wallId,
        Kind = kind,
        EntityId = Guid.NewGuid(),
        Audience = audience,
        AudienceUserId = audience == FeedAudience.User ? Guid.NewGuid() : null,
        DataJson = kind == FeedKinds.HoldChanged ? "{\"sv\":1}" : null,
    };
}
