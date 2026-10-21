// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Retention and paging on PostgreSQL, with transactions committing in between.</summary>
[Trait("Db", "Postgres")]
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresFeedRetentionTests
{
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-31);

    [PostgresFact]
    public async Task OldRowCommittingDuringRetention_IsNotDeletedAboveTheFloor_AndIsStillDelivered()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        await kit.Feed.InitializeAsync();
        var first = await kit.CommitAsync(kit.Row("old-1", createdAt: Old));

        // Retention deletes only below the horizon, which is cluster-wide: let it pass `first` before `late` opens.
        await PostgresHorizonWait.UntilCeilingReachesAsync(() => pg.CreateContext(), first.TxId!.Value);

        await using var late = pg.CreateContext();
        await using var lateTx = await late.Database.BeginTransactionAsync();
        var second = await PostgresFeedTestKit.InsertAsync(late, kit.Row("old-2", createdAt: Old));
        var firstRun = await RetainAsync(pg);
        await lateTx.CommitAsync();

        var fromFloor = await kit.DrainUntilAsync(await FloorCursorAsync(pg), expected: 1);
        await PostgresHorizonWait.UntilCeilingReachesAsync(() => pg.CreateContext(), second.TxId!.Value);
        var secondRun = await RetainAsync(pg);

        Assert.Equal(1, firstRun);
        Assert.Equal(["old-2"], fromFloor.Rows.Select(r => r.Kind));
        Assert.Equal(1, secondRun);
        Assert.Equal(FeedPosition.Of(second), (await FloorCursorAsync(pg)).Position);
        Assert.True(FeedPosition.Of(first) < FeedPosition.Of(second));
    }

    [PostgresFact]
    public async Task LimitOne_FullPagesStepRowByRow_AndAShortPageStopsAtTheHighWaterMark_AcrossInterleavedTransactions()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var start = await kit.Feed.CurrentAsync();
        var a = await kit.CommitAsync(kit.Row("a"));

        // The horizon is cluster-wide: it must pass `a` before T opens, so p1 is not held back by another database.
        await PostgresHorizonWait.UntilCeilingReachesAsync(() => pg.CreateContext(), a.TxId!.Value);

        // T takes the older xid and stays open; D commits after it.
        await using var t = pg.CreateContext();
        await using var txT = await t.Database.BeginTransactionAsync();
        await PostgresFeedTestKit.TakeXidAsync(t);
        var d = await kit.CommitAsync(kit.Row("d"));

        var p1 = await kit.Feed.ReadAsync(kit.Filter, start, 1);
        var p2 = await kit.Feed.ReadAsync(kit.Filter, p1.Next, 1);
        var tRow = await PostgresFeedTestKit.InsertAsync(t, kit.Row("t"));
        await txT.CommitAsync();
        await PostgresHorizonWait.UntilCeilingReachesAsync(() => pg.CreateContext(), d.TxId!.Value);
        var p3 = await kit.Feed.ReadAsync(kit.Filter, p2.Next, 1);
        var p4 = await kit.Feed.ReadAsync(kit.Filter, p3.Next, 1);
        var p5 = await kit.Feed.ReadAsync(kit.Filter, p4.Next, 1);

        Assert.Equal(["a"], p1.Changes.Select(c => c.Kind));
        Assert.Equal(FeedPosition.Of(a), p1.Next.Position);
        Assert.Empty(p2.Changes);
        Assert.Equal(FeedPosition.Of(a), p2.Next.Position);
        Assert.Equal(["t"], p3.Changes.Select(c => c.Kind));
        Assert.Equal(FeedPosition.Of(tRow), p3.Next.Position);
        Assert.Equal(["d"], p4.Changes.Select(c => c.Kind));
        Assert.Equal(FeedPosition.Of(d), p4.Next.Position);
        Assert.Empty(p5.Changes);
        Assert.Equal(FeedPosition.Of(d), p5.Next.Position);
    }

    private static async Task<int> RetainAsync(PostgresTestDatabase pg)
    {
        await using var db = pg.CreateContext();
        return await WallChangeRetention.RunAsync(db, DateTimeOffset.UtcNow - WallChangeRetention.Window, CancellationToken.None);
    }

    private static async Task<FeedCursor> FloorCursorAsync(PostgresTestDatabase pg)
    {
        await using var db = pg.CreateContext();
        var epoch = await db.FeedEpochs.SingleAsync();
        return new FeedCursor(epoch.Id, new FeedPosition(epoch.FloorTxId, epoch.FloorSeq));
    }
}
