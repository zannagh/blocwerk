// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.StructureFeedKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A resync written because mapping failed goes to every viewer of the wall, so it never names the saver: the failed entry
/// may be someone's tick or rating.
/// </summary>
public sealed class FeedFailureActorTests
{
    [Fact]
    public async Task AMapperFailureOnAWallRow_WritesAnActorlessResync()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];

        await using (var db = Throwing(harness, typeof(Hold)))
        {
            db.CurrentUserId = harness.Owner.Id;
            (await db.Holds.SingleAsync(h => h.Id == hold.Id)).X = 0.3;
            await db.SaveChangesAsync();
        }

        AssertActorlessResync(await RowsAsync(harness), harness);
    }

    [Fact]
    public async Task AMapperFailureOnABoulderPart_WritesAnActorlessResync()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 2);
        var boulder = await SeedBoulderAsync(harness, holds[0].Id);

        await using (var db = Throwing(harness, typeof(BoulderHold)))
        {
            db.CurrentUserId = harness.Owner.Id;
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holds[1].Id });
            await db.SaveChangesAsync();
        }

        AssertActorlessResync(await RowsAsync(harness), harness);
    }

    [Fact]
    public async Task AFailedBoulderLookup_WritesAnActorlessResync()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 2);
        var boulder = await SeedBoulderAsync(harness, holds[0].Id);

        await using (var db = TestDb.Create(harness.DbContextFactory.ConnectionString, Interceptor(), new BreakingLookupInterceptor()))
        {
            db.CurrentUserId = harness.Owner.Id;
            var tracked = await db.Holds.SingleAsync(h => h.Id == holds[1].Id);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = tracked.Id });
            await db.SaveChangesAsync();
        }

        AssertActorlessResync(await RowsAsync(harness), harness);
    }

    private static BlocwerkDbContext Throwing(WallTestHarness harness, Type entity) =>
        TestDb.Create(
            harness.DbContextFactory.ConnectionString,
            (IInterceptor)new FeedInterceptor(new FeedMapperRegistry([new ThrowingMapper(entity)]), new RecordingFeedSignal(), NullLogger<FeedInterceptor>.Instance));

    private static void AssertActorlessResync(List<WallChange> rows, WallTestHarness harness)
    {
        var row = Assert.Single(rows);
        Assert.Equal((FeedKinds.WallResync, (Guid?)harness.WallId), (row.Kind, row.WallId));
        Assert.Contains(FeedCollector.MappingFailedReason, row.DataJson);
        Assert.Null(row.ActorUserId);
    }
}
