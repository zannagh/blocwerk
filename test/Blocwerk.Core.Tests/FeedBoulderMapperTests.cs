// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.StructureFeedKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The boulder mappers: drafts stay with the setters, publishing and unpublishing reach the wall, and a boulder's parts
/// (holds, setters, move hints) write one <c>boulder.changed</c> per boulder, tracked or looked up, or a resync in a scope.
/// </summary>
public sealed class FeedBoulderMapperTests
{
    [Theory]
    [InlineData(false, FeedAudience.Wall)]
    [InlineData(true, FeedAudience.Setters)]
    public async Task ANewBoulder_GoesToTheWall_OrToTheSettersWhileADraft(bool isDraft, FeedAudience audience)
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1, generation: 2))[0];
        var boulder = new Boulder { WallId = harness.WallId, Name = "New", CreatedByUserId = harness.Owner.Id, IsDraft = isDraft, Generation = 2 };

        await using (var db = Context(harness))
        {
            db.Boulders.Add(boulder);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = hold.Id });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness)); // the boulder's own row covers its holds
        Assert.Equal((FeedKinds.BoulderChanged, boulder.Id, boulder.Id, audience), (row.Kind, row.BoulderId, row.EntityId, row.Audience));
        Assert.Equal(2, Payload(row).GetProperty("generation").GetInt32());
        Assert.Equal(isDraft, Payload(row).GetProperty("isDraft").GetBoolean());
    }

    [Theory]
    [InlineData(true, false, FeedAudience.Wall)] // published
    [InlineData(false, true, FeedAudience.Wall)] // taken back: viewers who saw it must learn it left
    [InlineData(true, true, FeedAudience.Setters)] // a draft edited
    public async Task ADraftFlip_ReachesTheWall_AndADraftEditStaysWithTheSetters(bool from, bool to, FeedAudience audience)
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var boulder = await SeedBoulderAsync(harness, hold.Id, isDraft: from);

        await using (var db = Context(harness))
        {
            var tracked = await db.Boulders.SingleAsync();
            (tracked.IsDraft, tracked.Name) = (to, "Edited");
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.BoulderChanged, boulder.Id, audience), (row.Kind, row.EntityId, row.Audience));
    }

    [Fact]
    public async Task ADeletedDraft_WritesBoulderDeleted_ForTheSetters()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var boulder = await SeedBoulderAsync(harness, hold.Id, isDraft: true);

        await using (var db = Context(harness))
        {
            db.Boulders.Remove(await db.Boulders.Include(b => b.BoulderHolds).SingleAsync());
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.BoulderDeleted, boulder.Id, FeedAudience.Setters), (row.Kind, row.EntityId, row.Audience));
    }

    [Fact]
    public Task PartsOfAnUntrackedBoulder_WriteOneRow_WithTheBoulderLookedUp_Sqlite() => UntrackedPartsAsync(new WallTestHarness());

    [Fact]
    public Task PartsOfAnUntrackedBoulder_AreLookedUp_OnASynchronousSaveToo() => UntrackedPartsAsync(new WallTestHarness(), sync: true);

    // The lookup is a query inside SavingChanges, on the saving context's own connection.
    [PostgresFact]
    public Task PartsOfAnUntrackedBoulder_WriteOneRow_WithTheBoulderLookedUp_Postgres() =>
        UntrackedPartsAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public async Task APartRemoved_WithItsBoulderTracked_NeedsNoLookup()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var boulder = await SeedBoulderAsync(harness, hold.Id);

        await using (var db = Context(harness))
        {
            var tracked = await db.Boulders.Include(b => b.BoulderHolds).SingleAsync();
            db.BoulderHolds.RemoveRange(tracked.BoulderHolds);
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.BoulderChanged, boulder.Id, FeedAudience.Wall), (row.Kind, row.EntityId, row.Audience));
    }

    [Fact]
    public async Task InsideAScope_PartsOfAnUntrackedBoulder_CollapseToOneResync()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 2);
        var boulder = await SeedBoulderAsync(harness, holds[0].Id);

        await using (var db = Context(harness))
        using (FeedScope.Begin(harness.WallId, "probe-op"))
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holds[1].Id });
            (await db.Holds.SingleAsync(h => h.Id == holds[1].Id)).X = 0.7;
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.WallResync, harness.WallId), (row.Kind, row.WallId!.Value));
        Assert.Contains("probe-op", row.DataJson);
    }

    [Fact]
    public async Task AThrowingPartMapper_ResyncsTheWallOfItsUntrackedBoulder_ThroughTheLookup()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 2);
        var boulder = await SeedBoulderAsync(harness, holds[0].Id);
        var feed = new FeedInterceptor(
            new FeedMapperRegistry([new ThrowingMapper(typeof(BoulderHold))]), new RecordingFeedSignal(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());

        await using (var db = TestDb.Create(harness.DbContextFactory.ConnectionString, feed))
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holds[1].Id });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.WallResync, harness.WallId), (row.Kind, row.WallId!.Value));
        Assert.Contains(FeedCollector.MappingFailedReason, row.DataJson);
    }

    [Fact]
    public async Task AMoveHint_TouchesItsBoulder()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var boulder = await SeedBoulderAsync(harness, hold.Id);

        await using (var db = Context(harness))
        {
            db.BoulderHoldMoves.Add(new BoulderHoldMove { WallId = harness.WallId, BoulderId = boulder.Id, OldHoldId = hold.Id, Outcome = HoldMoveOutcome.Kept });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.BoulderChanged, boulder.Id), (row.Kind, row.EntityId!.Value));
    }

    private static async Task UntrackedPartsAsync(WallTestHarness harness, bool sync = false)
    {
        using var owned = harness;
        var holds = await harness.SeedWallAsync(holdCount: 3);
        var boulder = await SeedBoulderAsync(harness, holds[0].Id, isDraft: true);

        await using (var db = Context(harness))
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holds[1].Id });
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holds[2].Id });
            db.BoulderSetters.Add(new BoulderSetter { BoulderId = boulder.Id, UserId = harness.Owner.Id });
            if (sync)
            {
                db.SaveChanges();
            }
            else
            {
                await db.SaveChangesAsync();
            }
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.BoulderChanged, boulder.Id, harness.WallId, FeedAudience.Setters), (row.Kind, row.EntityId, row.WallId!.Value, row.Audience));
        Assert.DoesNotContain(harness.Owner.Id.ToString(), row.DataJson);
    }
}
