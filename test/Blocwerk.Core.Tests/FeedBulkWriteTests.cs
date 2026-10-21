// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.FeedBulkKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="FeedBulkWrite"/>, the one path of the bulk sites: rows commit and roll back with the write, wakes follow the
/// commit, and a failing feed never fails the write, inside the caller's transaction or its own, on both providers.
/// </summary>
public sealed class FeedBulkWriteTests
{
    [Fact]
    public Task RowsAndWrite_CommitTogether_AndTheWakeFollowsTheCommit_Sqlite() => CommitAsync(new WallTestHarness());

    [PostgresFact]
    public Task RowsAndWrite_CommitTogether_AndTheWakeFollowsTheCommit_Postgres() => CommitAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public Task AFailedWrite_LeavesNoRow_Sqlite() => FailedWriteAsync(new WallTestHarness());

    [PostgresFact]
    public Task AFailedWrite_LeavesNoRow_Postgres() => FailedWriteAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public Task AFailingFeed_NeverFailsTheWrite_Sqlite() => FailingFeedAsync(new WallTestHarness());

    [PostgresFact]
    public Task AFailingFeed_NeverFailsTheWrite_Postgres() => FailingFeedAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public async Task InsideTheCallersTransaction_ARollbackTakesTheRowsAndTheWake()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var signal = new RecordingFeedSignal();
        await using (var db = Context(h, signal))
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await FlagAsync(db, holds[0]);
            await tx.RollbackAsync();
        }

        Assert.Empty(await RowsAsync(h));
        Assert.Empty(signal.Woken);
        await using var check = h.CreateContext();
        Assert.False(await check.Holds.Where(x => x.Id == holds[0].Id).Select(x => x.NeedsReview).SingleAsync());
    }

    [Fact]
    public async Task ManyRowsForOneWall_CollapseToOneResync()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await using var db = Context(h, new RecordingFeedSignal());
        await using var tx = await db.Database.BeginTransactionAsync();
        var rows = Enumerable.Range(0, FeedCollapse.MaxRowsPerWall + 1).Select(_ => FeedBulkRows.Gallery(h.WallId, Guid.NewGuid(), null));

        await FeedBulkWrite.AppendAsync(db, "probe", rows, NullLogger.Instance);
        await tx.CommitAsync();

        Assert.Equal(FeedKinds.WallResync, Assert.Single(await RowsAsync(h)).Kind);
    }

    private static async Task CommitAsync(WallTestHarness h)
    {
        using var owned = h;
        var holds = await h.SeedWallAsync(holdCount: 1);
        var signal = new RecordingFeedSignal();
        await using (var db = Context(h, signal))
        {
            Assert.Equal(1, await FlagAsync(db, holds[0]));
            Assert.Null(db.Database.CurrentTransaction);
        }

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((FeedKinds.HoldChanged, holds[0].Id), (row.Kind, row.EntityId!.Value));
        var wake = Assert.Single(signal.Woken);
        Assert.Equal((h.WallId, row.TxId), (wake.WallId, wake.TxId));
    }

    private static async Task FailedWriteAsync(WallTestHarness h)
    {
        using var owned = h;
        await h.SeedWallAsync(holdCount: 0);
        var signal = new RecordingFeedSignal();
        await using (var db = Context(h, signal))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => FeedBulkWrite.RunAsync<int>(
                db,
                "probe",
                () => throw new InvalidOperationException("write failed"),
                _ => [FeedBulkRows.Gallery(h.WallId, Guid.NewGuid(), null)],
                NullLogger.Instance,
                CancellationToken.None));
        }

        Assert.Empty(await RowsAsync(h));
        Assert.Empty(signal.Woken);
    }

    // On Postgres the broken insert aborts the transaction unless the savepoint contains it; the commit then proves it did.
    private static async Task FailingFeedAsync(WallTestHarness h)
    {
        using var owned = h;
        var holds = await h.SeedWallAsync(holdCount: 1);
        await using (var db = TestDb.Create(h.DbContextFactory.ConnectionString, StructureFeedKit.Interceptor(), new FailingFeedInsert()))
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.Equal(1, await FlagAsync(db, holds[0]));
            await db.Holds.Where(x => x.Id == holds[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.X, 0.25));
            await tx.CommitAsync();
        }

        await using var check = h.CreateContext();
        var hold = await check.Holds.AsNoTracking().SingleAsync(x => x.Id == holds[0].Id);
        Assert.True(hold.NeedsReview);
        Assert.Equal(0.25, hold.X);
        Assert.Empty(await RowsAsync(h));
    }

    private static Task<int> FlagAsync(BlocwerkDbContext db, Hold hold) =>
        FeedBulkWrite.RunAsync(
            db,
            "probe",
            () => db.Holds.Where(x => x.Id == hold.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NeedsReview, true)),
            n => n == 0 ? [] : [FeedBulkRows.Hold(hold.WallId, hold.Id, hold.Generation, hold.WallPanelId, null)],
            NullLogger.Instance,
            CancellationToken.None);

    private static BlocwerkDbContext Context(WallTestHarness h, RecordingFeedSignal signal) =>
        TestDb.Create(h.DbContextFactory.ConnectionString, StructureFeedKit.Interceptor(signal));
}
