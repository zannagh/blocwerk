// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="FeedInterceptor"/>: rows are part of the save (they commit and roll back with it), wakes follow the commit,
/// a failed save leaves nothing behind and a retry writes once, and a mapper bug never fails the save.
/// </summary>
public sealed class FeedInterceptorTests
{
    private readonly RecordingFeedSignal signal = new();

    [Fact]
    public Task APlainSave_WritesItsRowsInTheSameSave_AndWakesAfterIt_Sqlite() => PlainSaveAsync(new WallTestHarness());

    [PostgresFact]
    public Task APlainSave_WritesItsRowsInTheSameSave_AndWakesAfterIt_Postgres() => PlainSaveAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public Task AFailedSave_WritesNothing_AndARetryWritesOnce_Sqlite() => FailedSaveAsync(new WallTestHarness());

    [PostgresFact]
    public Task AFailedSave_WritesNothing_AndARetryWritesOnce_Postgres() => FailedSaveAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public Task ATransaction_WakesOnlyOnCommit_AndARollbackLeavesNoRow_Sqlite() => TransactionAsync(new WallTestHarness());

    [PostgresFact]
    public Task ATransaction_WakesOnlyOnCommit_AndARollbackLeavesNoRow_Postgres() => TransactionAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public async Task AMapperThatThrows_DoesNotFailTheSave_AndWritesAResyncOfTheEntrysWall()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];

        await using (var db = Context(harness, new ThrowingMapper(typeof(Hold))))
        {
            await MoveAsync(db, hold.Id);
        }

        var rows = await RowsAsync(harness);
        var row = Assert.Single(rows);
        Assert.Equal(FeedKinds.WallResync, row.Kind);
        Assert.Equal(harness.WallId, row.WallId);
        Assert.Contains(FeedCollector.MappingFailedReason, row.DataJson);
    }

    [Fact]
    public Task AOneEditSave_RunsWithItsRowInOneTransaction_Sqlite() => OneTransactionAsync(new WallTestHarness());

    [PostgresFact]
    public Task AOneEditSave_RunsWithItsRowInOneTransaction_Postgres() => OneTransactionAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public async Task WithNoMapper_ASaveWritesNothing()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];

        await using (var db = Context(harness))
        {
            await MoveAsync(db, hold.Id);
        }

        Assert.Empty(await RowsAsync(harness));
        Assert.Empty(signal.Woken);
    }

    [Fact]
    public async Task AWallLessRow_IsWritten_ButWakesNoWall()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);

        await using (var db = Context(harness, new ProbeUserMapper()))
        {
            var owner = await db.Users.SingleAsync(u => u.Id == harness.Owner.Id);
            owner.CustomDisplayName = "Renamed";
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal(FeedKinds.UserProfile, row.Kind);
        Assert.Null(row.WallId);
        Assert.Empty(signal.Woken);
    }

    [Fact]
    public async Task TheActor_IsTheContextsUser()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];

        await using (var db = Context(harness, new ProbeHoldMapper()))
        {
            db.CurrentUserId = harness.Owner.Id;
            await MoveAsync(db, hold.Id);
        }

        Assert.Equal(harness.Owner.Id, Assert.Single(await RowsAsync(harness)).ActorUserId);
    }

    private async Task PlainSaveAsync(WallTestHarness harness)
    {
        using var owned = harness;
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];

        await using var db = Context(harness, new ProbeHoldMapper());
        await MoveAsync(db, hold.Id);

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.HoldChanged, hold.Id, harness.WallId), (row.Kind, row.EntityId, row.WallId));
        var wake = Assert.Single(signal.Woken);
        Assert.Equal(new FeedWake(harness.WallId, row.TxId), wake);
        Assert.Equal(TestDb.IsPostgres(harness.DbContextFactory.ConnectionString), row.TxId is not null);
        Assert.Empty(db.ChangeTracker.Entries<WallChange>()); // detached once saved
    }

    // EF skips its transaction for a save of a single statement; the feed row makes it two, so edit and row are atomic
    // even without an explicit transaction.
    private async Task OneTransactionAsync(WallTestHarness harness)
    {
        using var owned = harness;
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var transactions = new TransactionCounter();
        var feed = new FeedInterceptor(new FeedMapperRegistry([new ProbeHoldMapper()]), signal, NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
        await using var db = TestDb.Create(harness.DbContextFactory.ConnectionString, feed, transactions);
        var tracked = await db.Holds.SingleAsync(h => h.Id == hold.Id);
        tracked.X += 0.01;

        await db.SaveChangesAsync();

        Assert.Equal(1, transactions.Started);
        Assert.Single(await RowsAsync(harness));
    }

    private async Task FailedSaveAsync(WallTestHarness harness)
    {
        using var owned = harness;
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];

        await using var db = Context(harness, new ProbeHoldMapper());
        var tracked = await db.Holds.SingleAsync(h => h.Id == hold.Id);
        tracked.X += 0.01;
        var duplicate = new User { Identifier = harness.Owner.Identifier, DisplayName = "Duplicate" };
        db.Users.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Empty(await RowsAsync(harness));
        Assert.Empty(signal.Woken);
        Assert.Empty(db.ChangeTracker.Entries<WallChange>());

        db.Entry(duplicate).State = EntityState.Detached;
        await db.SaveChangesAsync();
        Assert.Single(await RowsAsync(harness));
        Assert.Single(signal.Woken);
    }

    private async Task TransactionAsync(WallTestHarness harness)
    {
        using var owned = harness;
        var holds = await harness.SeedWallAsync(holdCount: 2);

        await using var db = Context(harness, new ProbeHoldMapper());
        await using (var rolledBack = await db.Database.BeginTransactionAsync())
        {
            await MoveAsync(db, holds[0].Id);
            await rolledBack.RollbackAsync();
        }

        Assert.Empty(await RowsAsync(harness));
        Assert.Empty(signal.Woken);

        await using (var committed = await db.Database.BeginTransactionAsync())
        {
            await MoveAsync(db, holds[1].Id);
            await MoveAsync(db, holds[1].Id);
            Assert.Empty(signal.Woken);
            await committed.CommitAsync();
        }

        var rows = await RowsAsync(harness);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(holds[1].Id, r.EntityId));
        Assert.Equal(harness.WallId, Assert.Single(signal.Woken).WallId); // one wake per wall, not per save
    }

    private BlocwerkDbContext Context(WallTestHarness harness, params IFeedMapper[] mappers) =>
        TestDb.Create(
            harness.DbContextFactory.ConnectionString,
            new FeedInterceptor(new FeedMapperRegistry(mappers), signal, NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved()));

    private static async Task MoveAsync(BlocwerkDbContext db, Guid holdId)
    {
        var hold = await db.Holds.SingleAsync(h => h.Id == holdId);
        hold.X += 0.01;
        await db.SaveChangesAsync();
    }

    private static async Task<List<WallChange>> RowsAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        return await db.WallChanges.AsNoTracking().OrderBy(c => c.Seq).ToListAsync();
    }
}
