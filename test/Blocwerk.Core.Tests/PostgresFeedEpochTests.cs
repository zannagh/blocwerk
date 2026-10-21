// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The feed's epoch reset after a restore, the provider-conditional <c>TxId</c> DDL, and bulk appends, on PostgreSQL.</summary>
[Trait("Db", "Postgres")]
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresFeedEpochTests
{
    [PostgresFact]
    public async Task TransactionIdsFromAnotherCluster_TruncateTheFeed_StartANewEpoch_AndOldCursorsResync()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var oldEpoch = await kit.Feed.InitializeAsync();
        await kit.CommitAsync(kit.Row("before-restore"));
        var oldCursor = await kit.Feed.CurrentAsync();

        // What a restore into a younger cluster looks like: stored ids at or above the cluster's current xid.
        await using (var db = pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"WallChanges\" (\"TxId\", \"WallId\", \"Kind\", \"Audience\", \"SchemaVersion\", \"CreatedAt\") "
                + "VALUES (pg_current_xact_id()::text::bigint + 1000000, {0}, 'foreign', 0, 1, now())",
                kit.WallId);
        }

        // The startup check of a fresh process.
        var restarted = new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance);
        var newEpoch = await restarted.InitializeAsync();
        var page = await restarted.ReadAsync(kit.Filter, oldCursor, 100);

        Assert.NotEqual(oldEpoch, newEpoch);
        await using (var db = pg.CreateContext())
        {
            Assert.Equal(0, await db.WallChanges.CountAsync());
            Assert.Equal(newEpoch, (await db.FeedEpochs.SingleAsync()).Id);
        }

        Assert.True(page.Resync);
        Assert.Equal(newEpoch, page.Next.Epoch);
    }

    [PostgresFact]
    public async Task OwnClustersTransactionIds_KeepTheEpochAndTheRows()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var epoch = await kit.Feed.InitializeAsync();
        var start = await kit.Feed.CurrentAsync();
        await kit.CommitAsync(kit.Row("kept"));

        var restarted = new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance);

        Assert.Equal(epoch, await restarted.InitializeAsync());
        Assert.Equal(["kept"], (await restarted.ReadAsync(kit.Filter, start, 100)).Changes.Select(c => c.Kind));
    }

    [PostgresFact]
    public async Task TxId_IsTheWritingTransactionsXid_SetByTheColumnDefault_AndNotNull()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);

        await using var db = pg.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var xid = await PostgresFeedTestKit.TakeXidAsync(db);
        var row = await PostgresFeedTestKit.InsertAsync(db, kit.Row("saved"));
        await tx.CommitAsync();

        Assert.Equal(xid, row.TxId);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"WallChanges\" (\"TxId\", \"Kind\", \"Audience\", \"SchemaVersion\", \"CreatedAt\") VALUES (NULL, 'x', 0, 1, now())"));
    }

    [PostgresFact]
    public async Task Append_InsideATransaction_ReturnsItsXid_AndCommitsAndRollsBackWithIt()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var start = await kit.Feed.CurrentAsync();

        await using (var db = pg.CreateContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Feed.AppendAsync(db, kit.Row("no-tx")));

            await using var rolledBack = await db.Database.BeginTransactionAsync();
            await kit.Feed.AppendAsync(db, kit.Row("rolled-back"));
            await rolledBack.RollbackAsync();
        }

        long? txId;
        await using (var db = pg.CreateContext())
        {
            await using var committed = await db.Database.BeginTransactionAsync();
            var row = kit.Row("bulk");
            row.DataJson = "{\"generation\":3}";
            txId = await kit.Feed.AppendAsync(db, row);
            await committed.CommitAsync();
        }

        var read = await kit.DrainUntilAsync(start, expected: 1);
        var served = Assert.Single(read.Rows);
        Assert.Equal("bulk", served.Kind);
        Assert.Equal(txId, served.TxId);
        Assert.Contains("\"generation\": 3", served.DataJson);
    }

    [PostgresFact]
    public async Task ConcurrentInitialize_AgreesOnOneEpoch()
    {
        using var pg = PostgresTestDatabase.Create();
        var feeds = Enumerable.Range(0, 8).Select(_ => new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance)).ToList();

        var epochs = await Task.WhenAll(feeds.Select(f => Task.Run(() => f.InitializeAsync())));

        Assert.Single(epochs.Distinct());
        await using var db = pg.CreateContext();
        Assert.Equal(1, await db.FeedEpochs.CountAsync());
    }

    [PostgresFact]
    public async Task Check_InATransactionWhoseXidPredatesACommittedRow_DoesNotTruncate()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var epoch = await kit.Feed.InitializeAsync();

        // The check's transaction holds an older xid than the row another connection commits before the check runs:
        // compared with the transaction's own xid, that row would look foreign.
        await using var db = pg.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var ownXid = await PostgresFeedTestKit.TakeXidAsync(db);
        var row = await kit.CommitAsync(kit.Row("newer"));
        var checkedEpoch = await FeedEpochStore.EnsureAsync(db, NullLogger.Instance, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.True(row.TxId > ownXid);
        Assert.Equal(epoch, checkedEpoch.Id);
        await using var check = pg.CreateContext();
        Assert.Equal(1, await check.WallChanges.CountAsync());
    }
}
