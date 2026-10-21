// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The feed's no-skip guarantee on a real PostgreSQL server: reads are capped below the snapshot's <c>xmin</c>, so a row is
/// served once, in <c>(TxId, Seq)</c> order, however its transaction interleaves with others. One connection per context.
/// </summary>
[Trait("Db", "Postgres")]
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresFeedOrderingTests
{
    [PostgresFact]
    public async Task ReviewersInterleaving_LowerXidWithHigherSeqCommitsFirst_BothRowsAreServedOnceInOrder()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var start = await kit.Feed.CurrentAsync();

        // A takes its xid from its edit, before its feed row.
        await using var a = pg.CreateContext();
        await using var txA = await a.Database.BeginTransactionAsync();
        var xidA = await PostgresFeedTestKit.TakeXidAsync(a);

        // B (a later xid) takes the lower seq and stays open.
        await using var b = pg.CreateContext();
        await using var txB = await b.Database.BeginTransactionAsync();
        var rowB = await PostgresFeedTestKit.InsertAsync(b, kit.Row("b"));

        // A takes the higher seq and commits.
        var rowA = await PostgresFeedTestKit.InsertAsync(a, kit.Row("a"));
        await txA.CommitAsync();

        Assert.Equal(xidA, rowA.TxId);
        Assert.True(rowA.TxId < rowB.TxId, "A must hold the lower xid");
        Assert.True(rowA.Seq > rowB.Seq, "A must hold the higher seq");

        var whileBOpen = await kit.DrainUntilAsync(start, expected: 1);
        await txB.CommitAsync();
        var afterB = await kit.DrainUntilAsync(whileBOpen.Next, expected: 1);

        Assert.Equal(["a"], whileBOpen.Rows.Select(r => r.Kind));
        Assert.Equal(["b"], afterB.Rows.Select(r => r.Kind));
        var all = whileBOpen.Rows.Concat(afterB.Rows).Select(FeedPosition.Of).ToList();
        Assert.Equal(all.Order().ToList(), all);
    }

    [PostgresFact]
    public async Task RolledBackTransaction_LeavesNoRow_AndDoesNotStallTheHorizonAfterItEnds()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var start = await kit.Feed.CurrentAsync();

        await using (var r = pg.CreateContext())
        {
            await using var tx = await r.Database.BeginTransactionAsync();
            await PostgresFeedTestKit.InsertAsync(r, kit.Row("rolled-back"));
            await tx.RollbackAsync();
        }

        await kit.CommitAsync(kit.Row("after"));
        var read = await kit.DrainUntilAsync(start, expected: 1);

        Assert.Equal(["after"], read.Rows.Select(r => r.Kind));
    }

    [PostgresFact]
    public async Task LongOpenOlderTransaction_HoldsDeliveryBack_AndDeliveryResumesInXidOrderWhenItEnds()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var start = await kit.Feed.CurrentAsync();

        await using var t = pg.CreateContext();
        await using var txT = await t.Database.BeginTransactionAsync();
        await PostgresFeedTestKit.TakeXidAsync(t);
        await kit.CommitAsync(kit.Row("held", createdAt: DateTimeOffset.UtcNow.AddSeconds(-5)));

        var held = await kit.DrainAsync(start);
        double lagWhileHeld;
        await using (var db = pg.CreateContext())
        {
            lagWhileHeld = await FeedLagCollector.MeasureAsync(db, CancellationToken.None);
        }

        // T, the older xid, writes a row of its own last: a cursor that had moved past "held" would now skip it.
        await PostgresFeedTestKit.InsertAsync(t, kit.Row("older"));
        await txT.CommitAsync();
        var resumed = await kit.DrainUntilAsync(held.Next, expected: 2);
        double lagAfter;
        await using (var db = pg.CreateContext())
        {
            lagAfter = await FeedLagCollector.MeasureAsync(db, CancellationToken.None);
        }

        Assert.Empty(held.Rows);
        Assert.True(lagWhileHeld >= 4, $"the lag gauge must see the held row (was {lagWhileHeld:0.00}s)");
        Assert.Equal(["older", "held"], resumed.Rows.Select(r => r.Kind));
        Assert.Equal(0, lagAfter);
    }

    [PostgresFact]
    public async Task CursorAtLoad_TakenWhileAWriterIsOpen_TheWritersRowIsDeliveredAfterTheLoad()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);

        // The writer is open while the cursor is taken, so the load may miss it and its row must be delivered after it.
        await using (var c = pg.CreateContext())
        {
            await using var txC = await c.Database.BeginTransactionAsync();
            var user = new User { Identifier = "open@test", DisplayName = "Open" };
            c.Users.Add(user);
            await PostgresFeedTestKit.InsertAsync(c, kit.Row("user.added", user.Id));
            var cursorAtLoad = await kit.Feed.CurrentAsync();
            await txC.CommitAsync();

            var delivered = await kit.DrainUntilAsync(cursorAtLoad, expected: 1);
            Assert.Contains(delivered.Rows, r => r.EntityId == user.Id);
        }
    }

    [PostgresFact]
    public async Task CursorAtLoad_RacingCommits_NothingFallsBetweenTheLoadAndTheReadsAfterIt()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var written = new List<Guid>();
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                await using var w = pg.CreateContext();
                var user = new User { Identifier = $"race-{i}@test", DisplayName = "Race" };
                w.Users.Add(user);
                await PostgresFeedTestKit.InsertAsync(w, kit.Row("user.added", user.Id));
                lock (written)
                {
                    written.Add(user.Id);
                }
            }
        });

        var cursor = await kit.Feed.CurrentAsync();
        var loaded = new HashSet<Guid>(await LoadUserIdsAsync(pg));
        var seen = new HashSet<Guid>();
        while (!writer.IsCompleted)
        {
            var page = await kit.DrainAsync(cursor);
            seen.UnionWith(page.Rows.Select(r => r.EntityId!.Value));
            cursor = page.Next;
        }

        await writer;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (written.Any(id => !loaded.Contains(id) && !seen.Contains(id)) && DateTime.UtcNow < deadline)
        {
            var page = await kit.DrainAsync(cursor);
            seen.UnionWith(page.Rows.Select(r => r.EntityId!.Value));
            cursor = page.Next;
            await Task.Delay(50);
        }

        Assert.Equal(40, written.Count);
        Assert.All(written, id => Assert.True(loaded.Contains(id) || seen.Contains(id), $"{id} was lost"));
    }

    private static async Task<List<Guid>> LoadUserIdsAsync(PostgresTestDatabase pg)
    {
        await using var db = pg.CreateContext();
        return await db.Users.Select(u => u.Id).ToListAsync();
    }
}
