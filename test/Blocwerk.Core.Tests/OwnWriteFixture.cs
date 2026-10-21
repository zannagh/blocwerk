// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// One circuit's <see cref="WallCacheState"/> over services whose contexts carry the production feed interceptor and
/// mappers, as in the app. The interceptor's wakes go to a signal the hub never reads, so nothing reaches the circuit
/// through a delivery: whatever the cache gets right after a save came from the synchronous own-write path
/// (<see cref="FeedCommits"/>). <see cref="Request"/> builds a second set of services over the same singletons, the way
/// a JS→API request gets its own scope.
/// </summary>
internal sealed class OwnWriteFixture : IAsyncDisposable
{
    private readonly List<WallCacheChange> changes = [];

    public OwnWriteFixture(PostgresTestDatabase? postgres = null)
    {
        Harness = new WallTestHarness(postgres);
        var connectionString = Harness.DbContextFactory.ConnectionString;
        Interceptor = new FeedInterceptor(
            new FeedMapperRegistry(FeedMappers.Create()), new RecordingFeedSignal(), NullLogger<FeedInterceptor>.Instance, Recorded.Commits);
        Circuit = Request();
        var feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        Hub = new WallFeedHub(feed, new InProcessFeedSignal(), () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
        Link = new WallFeedLink(Hub, feed, Viewer, NullLogger<WallFeedLink>.Instance);
        Cache = new WallCacheState(Circuit.Walls, Circuit.Boulders, Recorded.Commits, Link, NullLogger<WallCacheState>.Instance);
        Cache.Changed += c =>
        {
            lock (changes)
            {
                changes.Add(c);
            }
        };
    }

    public WallTestHarness Harness { get; }

    public RecordingFeedCommits Recorded { get; } = new();

    public FeedInterceptor Interceptor { get; }

    public FakeCircuitFeedViewer Viewer { get; } = new();

    public WallFeedHub Hub { get; }

    public WallFeedLink Link { get; }

    public WallCacheState Cache { get; }

    /// <summary>Gets the circuit's own services (its Blazor handlers save through these).</summary>
    public OwnWriteServices Circuit { get; }

    public Guid WallId => Harness.WallId;

    public IReadOnlyList<WallCacheChange> Changes
    {
        get
        {
            lock (changes)
            {
                return changes.ToList();
            }
        }
    }

    public async Task<List<Hold>> StartAsync(int holdCount = 3)
    {
        var holds = await Harness.SeedWallAsync(holdCount);
        Viewer.Viewer = FeedViewer.Setter(Harness.Owner.Id);
        await Hub.StartAsync(CancellationToken.None);
        return holds;
    }

    /// <summary>A fresh set of services over the same interceptor: another request scope.</summary>
    public OwnWriteServices Request() =>
        new(new InterceptedDbContextFactory(Harness.DbContextFactory.ConnectionString, [Interceptor]), Harness);

    /// <summary>The wall straight from the database, bypassing every cache.</summary>
    public async Task<Wall> DatabaseWallAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.Walls.AsNoTracking().Include(w => w.Holds).Include(w => w.Boulders).SingleAsync(w => w.Id == WallId);
    }

    public async ValueTask DisposeAsync()
    {
        Cache.Dispose();
        Link.Dispose();
        await Hub.StopAsync(CancellationToken.None);
        Hub.Dispose();
        Harness.Dispose();
    }
}
