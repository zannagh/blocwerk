// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Feed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// One circuit's <see cref="WallCacheState"/> over a real <see cref="WallFeedHub"/> (SQLite, or Postgres when given): the
/// harness's services load, the test writes feed rows straight into <c>WallChanges</c> and wakes the hub, and the cache's
/// events are recorded.
/// </summary>
internal sealed class CircuitCacheFixture : IAsyncDisposable
{
    private readonly List<WallCacheChange> changes = [];
    private readonly List<FeedDelivery> deliveries = [];
    private readonly List<Guid> lost = [];

    public CircuitCacheFixture(
        PostgresTestDatabase? postgres = null,
        Func<WallTestHarness, ICircuitFeedViewer>? viewers = null,
        Func<IWallFeedHub, IWallFeedHub>? wrapHub = null)
    {
        Harness = new WallTestHarness(postgres);
        var connectionString = Harness.DbContextFactory.ConnectionString;
        Feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        Hub = new WallFeedHub(Feed, Signal, () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
        Viewers = viewers?.Invoke(Harness) ?? Fake;
        Link = new WallFeedLink(wrapHub?.Invoke(Hub) ?? Hub, Feed, Viewers, NullLogger<WallFeedLink>.Instance);
        Cache = new WallCacheState(Harness.WallService, Harness.BoulderService, Commits, Link);
        Cache.Changed += c => Record(changes, c);
        Cache.FeedDelivered += d => Record(deliveries, d);
        Cache.AccessLost += w => Record(lost, w);
    }

    public WallTestHarness Harness { get; }

    public InProcessFeedSignal Signal { get; } = new();

    public FeedCommits Commits { get; } = new(NullLogger<FeedCommits>.Instance);

    public WallFeed Feed { get; }

    public WallFeedHub Hub { get; }

    public FakeCircuitFeedViewer Fake { get; } = new();

    public ICircuitFeedViewer Viewers { get; }

    public WallFeedLink Link { get; }

    public WallCacheState Cache { get; }

    public List<Hold> Holds { get; private set; } = [];

    public Guid WallId => Harness.WallId;

    public IReadOnlyList<WallCacheChange> Changes => Snapshot(changes);

    public IReadOnlyList<FeedDelivery> Deliveries => Snapshot(deliveries);

    public IReadOnlyList<Guid> Lost => Snapshot(lost);

    public WallCacheStore Store => Cache.Store;

    public async Task StartAsync(int holdCount = 2)
    {
        Holds = await Harness.SeedWallAsync(holdCount);
        Fake.Viewer = FeedViewer.Setter(Harness.Owner.Id);
        await Hub.StartAsync(CancellationToken.None);
    }

    public async Task<Boulder> AddBoulderAsync(string name, params Hold[] holds)
    {
        await using var db = Harness.CreateContext();
        var boulder = new Boulder { WallId = WallId, Name = name, CreatedByUserId = Harness.Owner.Id };
        db.Boulders.Add(boulder);
        db.BoulderHolds.AddRange(holds.Select(h => new BoulderHold { BoulderId = boulder.Id, HoldId = h.Id }));
        await db.SaveChangesAsync();
        return boulder;
    }

    /// <summary>Caches the aggregate, both generations' hold sets and the given boulders.</summary>
    public async Task WarmAsync(params Boulder[] boulders)
    {
        await Cache.GetWallAsync(WallId);
        await Cache.GetHoldsForGenerationAsync(WallId, 0);
        await Cache.GetHoldsForGenerationAsync(WallId, 1);
        foreach (var boulder in boulders)
        {
            await Cache.GetBoulderAsync(boulder.Id);
        }
    }

    public WallChange Row(
        string kind, Guid? entityId = null, FeedPayload? data = null, FeedAudience audience = FeedAudience.Wall,
        Guid? user = null, string? rawData = null) => new()
    {
        WallId = WallId,
        Kind = kind,
        EntityId = entityId ?? Guid.NewGuid(),
        DataJson = rawData ?? (data is null ? null : FeedJson.Serialize(data)),
        Audience = audience,
        AudienceUserId = user,
    };

    /// <summary>Commits the rows; wakes the hub unless told not to.</summary>
    public async Task CommitAsync(bool wake, params WallChange[] rows)
    {
        await using var db = Harness.CreateContext();
        db.WallChanges.AddRange(rows);
        await db.SaveChangesAsync();
        if (wake)
        {
            Signal.Wake(new FeedWake(WallId, rows[^1].TxId));
        }
    }

    /// <summary>Commits the rows, wakes the hub and waits until the circuit received one more delivery.</summary>
    public async Task DeliverAsync(params WallChange[] rows)
    {
        var before = Deliveries.Count;
        await CommitAsync(true, rows);
        await WaitUntilAsync(() => Deliveries.Count > before);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cache.Dispose();
        Link.Dispose();
        await Hub.StopAsync(CancellationToken.None);
        Hub.Dispose();
        Harness.Dispose();
    }

    private static void Record<T>(List<T> list, T item)
    {
        lock (list)
        {
            list.Add(item);
        }
    }

    private static List<T> Snapshot<T>(List<T> list)
    {
        lock (list)
        {
            return list.ToList();
        }
    }
}
