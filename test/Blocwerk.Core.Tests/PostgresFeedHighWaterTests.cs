// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// No cursor is handed out above the stored high-water mark (max of the newest row and the retention floor), so the
/// startup restore check, which compares that mark with the cluster's xid counter, sees every cursor a client can hold.
/// </summary>
[Trait("Db", "Postgres")]
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresFeedHighWaterTests
{
    private const int BurnedXids = 50;

    [PostgresFact]
    public async Task QuietWallCursor_StaysAtTheHighWaterMark_WhileTheHorizonMovesOn_AndReReadsNothing()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var elsewhere = kit.Row("elsewhere");
        elsewhere.WallId = Guid.NewGuid();
        var otherWall = FeedPosition.Of(await kit.CommitAsync(elsewhere));
        await BurnXidsAsync(pg);

        var quiet = await kit.DrainAsync(await kit.Feed.CurrentAsync());
        var again = await kit.DrainAsync(quiet.Next);
        var ceiling = await CeilingAsync(pg);
        var mine = await kit.CommitAsync(kit.Row("mine"));
        var next = await kit.DrainUntilAsync(again.Next, expected: 1);

        Assert.Empty(quiet.Rows);
        Assert.Equal(otherWall, quiet.Next.Position);
        Assert.True(ceiling > quiet.Next.Position, "the horizon moved on, the cursor did not follow it past the stored rows");
        Assert.Empty(again.Rows);
        Assert.Equal(quiet.Next, again.Next);
        Assert.Equal(["mine"], next.Rows.Select(r => r.Kind));
        Assert.Equal(FeedPosition.Of(mine), next.Next.Position);
    }

    [PostgresFact]
    public async Task RestoreWithTheCounterBetweenTheStoredMaxAndTheOldCeiling_SkipsNothing()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var start = await kit.Feed.CurrentAsync();
        var row = await kit.CommitAsync(kit.Row("before-restore"));
        await BurnXidsAsync(pg);
        var client = (await kit.DrainAsync(start)).Next;
        var oldCeiling = await CeilingAsync(pg);

        // Renumber the stored ids (and the client's cursor) as if they came from an older cluster whose max id sits just
        // below this cluster's counter, while that cluster's ceiling was above it.
        var counter = await CurrentXidAsync(pg);
        var shift = counter - row.TxId!.Value - 10;
        await using (var db = pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"WallChanges\" SET \"TxId\" = \"TxId\" + {0}", shift);
        }

        var restoredClient = client with { Position = client.Position with { TxId = client.Position.TxId + shift } };
        var restarted = new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance);
        var epoch = await restarted.InitializeAsync();
        var fresh = await kit.CommitAsync(kit.Row("after-restore"));
        var read = await kit.DrainUntilAsync(restoredClient, expected: 1);

        Assert.True(new FeedPosition(oldCeiling.TxId + shift, oldCeiling.Seq) > FeedPosition.Of(fresh), "a ceiling cursor would skip the new row");
        Assert.Equal(start.Epoch, epoch);
        Assert.Equal(["after-restore"], read.Rows.Select(r => r.Kind));
    }

    [PostgresFact]
    public async Task StartupCheck_CountsTheRetentionFloor_WhenRetentionDeletedEveryRow()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var oldEpoch = await kit.Feed.InitializeAsync();
        await using (var db = pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"FeedEpochs\" SET \"FloorTxId\" = pg_current_xact_id()::text::bigint + 1000000, \"FloorSeq\" = 7");
        }

        var restarted = new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance);

        Assert.NotEqual(oldEpoch, await restarted.InitializeAsync());
    }

    // Each autocommit statement that asks for its xid consumes one, so the horizon moves past the stored rows.
    private static async Task BurnXidsAsync(PostgresTestDatabase pg)
    {
        await using var db = pg.CreateContext();
        for (var i = 0; i < BurnedXids; i++)
        {
            await CurrentXidAsync(db);
        }
    }

    private static async Task<long> CurrentXidAsync(PostgresTestDatabase pg)
    {
        await using var db = pg.CreateContext();
        return await CurrentXidAsync(db);
    }

    private static Task<long> CurrentXidAsync(BlocwerkDbContext db) =>
        db.Database.SqlQueryRaw<long>("SELECT pg_current_xact_id()::text::bigint AS \"Value\"").SingleAsync();

    private static async Task<FeedPosition> CeilingAsync(PostgresTestDatabase pg)
    {
        await using var db = pg.CreateContext();
        return await PostgresFeedHorizon.Instance.CeilingAsync(db, CancellationToken.None);
    }
}
