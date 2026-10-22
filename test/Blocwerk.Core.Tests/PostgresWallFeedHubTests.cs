// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hub on Postgres: a save through the interceptor wakes it and reaches a subscriber, and a woken row held back by an
/// older open transaction is re-polled and delivered exactly once when that transaction ends.
/// </summary>
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresWallFeedHubTests
{
    [PostgresFact]
    public async Task ASaveThroughTheInterceptor_ReachesASubscriber()
    {
        using var harness = new WallTestHarness(PostgresTestDatabase.Create());
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var signal = new InProcessFeedSignal();
        using var hub = Hub(harness.DbContextFactory.ConnectionString, signal);
        await hub.StartAsync(CancellationToken.None);
        var subscriber = new RecordingFeedSubscriber();
        using var subscription = await hub.SubscribeAsync(harness.WallId, FeedViewer.Anonymous(), subscriber);

        var interceptor = new FeedInterceptor(new FeedMapperRegistry([new ProbeHoldMapper()]), signal, NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
        await using (var db = TestDb.Create(harness.DbContextFactory.ConnectionString, interceptor))
        {
            var tracked = await db.Holds.SingleAsync(h => h.Id == hold.Id);
            tracked.X += 0.01;
            await db.SaveChangesAsync();
        }

        var events = await subscriber.WaitForEventsAsync(1);
        Assert.Equal((FeedKinds.HoldChanged, hold.Id), (Assert.Single(events).Kind, events[0].EntityId));
        await hub.StopAsync(CancellationToken.None);
    }

    [PostgresFact]
    public async Task AWokenRowHeldByAnOlderOpenTransaction_IsDeliveredOnce_WhenItEnds()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var signal = new InProcessFeedSignal();
        using var hub = Hub(pg.ConnectionString, signal);
        await hub.StartAsync(CancellationToken.None);
        var subscriber = new RecordingFeedSubscriber();
        using var subscription = await hub.SubscribeAsync(kit.WallId, FeedViewer.Anonymous(), subscriber);

        await using var older = pg.CreateContext();
        await using var transaction = await older.Database.BeginTransactionAsync();
        await PostgresFeedTestKit.TakeXidAsync(older);
        var row = await kit.CommitAsync(kit.Row("held"));
        signal.Wake(new FeedWake(kit.WallId, row.TxId));

        await Task.Delay(TimeSpan.FromSeconds(1.5)); // the hub has read (and re-polled) meanwhile: the row is not servable
        Assert.Empty(subscriber.Events);

        await transaction.CommitAsync();
        Assert.Equal("held", Assert.Single(await subscriber.WaitForEventsAsync(1, seconds: 20)).Kind);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(subscriber.Events);
        await hub.StopAsync(CancellationToken.None);
    }

    private static WallFeedHub Hub(string connectionString, IFeedSignal signal) => new(
        new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance),
        signal,
        () => TestDb.Create(connectionString),
        NullLogger<WallFeedHub>.Instance);
}
