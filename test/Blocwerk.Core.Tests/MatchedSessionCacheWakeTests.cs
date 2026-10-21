// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="MatchedSessionCache"/> on the hub's wall wakes (PR 1.4): the reused holds-and-panels part of a fingerprint
/// gives way once the hub drains a wake of that wall, with no domain-change notifier at all.
/// </summary>
public sealed class MatchedSessionCacheWakeTests : IAsyncLifetime
{
    private readonly WallTestHarness harness = new();
    private readonly InProcessFeedSignal signal = new();
    private readonly WallFeedHub hub;

    public MatchedSessionCacheWakeTests()
    {
        var connectionString = harness.DbContextFactory.ConnectionString;
        var feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        hub = new WallFeedHub(feed, signal, () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
    }

    public async Task InitializeAsync()
    {
        await harness.SeedWallAsync(holdCount: 2);
        await hub.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await hub.StopAsync(CancellationToken.None);
        hub.Dispose();
        harness.Dispose();
    }

    [Fact]
    public async Task AReusedFingerprint_GivesWayToAWakeOfTheWall()
    {
        var cache = new MatchedSessionCache(feed: hub);
        var session = new BigUpdateSession(harness.WallId, Guid.NewGuid(), [], [], [], []);
        await using var db = harness.CreateContext();
        await cache.GetOrMatchAsync(db, harness.WallId, false, () => Task.FromResult(session));

        await using (var edit = harness.CreateContext())
        {
            var hold = await edit.Holds.FirstAsync(h => h.WallId == harness.WallId);
            hold.X += 0.05;
            await edit.SaveChangesAsync();
        }

        Assert.Same(session, await cache.PeekAsync(db, harness.WallId)); // no wake yet: the geometry part is reused

        signal.Wake(new FeedWake(harness.WallId, null));
        await CircuitCacheFixture.WaitUntilAsync(() => cache.PeekAsync(db, harness.WallId).GetAwaiter().GetResult() is null);
    }

    [Fact]
    public async Task AWakeOfAnotherWall_KeepsTheReuse()
    {
        var cache = new MatchedSessionCache(feed: hub);
        var observed = new List<Guid>();
        using var observation = hub.ObserveWakes(w =>
        {
            lock (observed)
            {
                observed.Add(w);
            }
        });
        var session = new BigUpdateSession(harness.WallId, Guid.NewGuid(), [], [], [], []);
        await using var db = harness.CreateContext();
        await cache.GetOrMatchAsync(db, harness.WallId, false, () => Task.FromResult(session));

        var other = Guid.NewGuid();
        signal.Wake(new FeedWake(other, null));
        await CircuitCacheFixture.WaitUntilAsync(() =>
        {
            lock (observed)
            {
                return observed.Contains(other);
            }
        });

        Assert.Same(session, await cache.PeekAsync(db, harness.WallId));
    }
}
