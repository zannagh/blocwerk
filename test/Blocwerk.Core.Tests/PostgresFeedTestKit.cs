// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Shared pieces of the Postgres feed tests: one wall's feed, rows, and the open-transaction moves.</summary>
internal sealed class PostgresFeedTestKit
{
    public PostgresFeedTestKit(PostgresTestDatabase pg)
    {
        Pg = pg;
        Feed = new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance);
    }

    public PostgresTestDatabase Pg { get; }

    public WallFeed Feed { get; }

    public Guid WallId { get; } = Guid.NewGuid();

    public FeedFilter Filter => FeedFilter.ForWall(WallId, [FeedAudience.Wall], null);

    public WallChange Row(string kind, Guid? entityId = null, DateTimeOffset? createdAt = null) =>
        new() { WallId = WallId, Kind = kind, EntityId = entityId ?? Guid.NewGuid(), CreatedAt = createdAt ?? DateTimeOffset.UtcNow };

    /// <summary>Gives the context's open transaction its xid through a write that has nothing to do with the feed.</summary>
    public static async Task<long> TakeXidAsync(BlocwerkDbContext db)
    {
        db.Users.Add(new User { Identifier = $"unrelated-{Guid.NewGuid():N}@test", DisplayName = "Unrelated" });
        await db.SaveChangesAsync();
        return await db.Database.SqlQueryRaw<long>("SELECT pg_current_xact_id()::text::bigint AS \"Value\"").SingleAsync();
    }

    /// <summary>Inserts a row through <c>SaveChanges</c>; EF reads the database-generated <c>TxId</c> and <c>Seq</c> back.</summary>
    public static async Task<WallChange> InsertAsync(BlocwerkDbContext db, WallChange row)
    {
        db.WallChanges.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public async Task<WallChange> CommitAsync(WallChange row)
    {
        await using var db = Pg.CreateContext();
        return await InsertAsync(db, row);
    }

    /// <summary>
    /// Reads until at least <paramref name="expected"/> rows arrived (or 10 s passed). The horizon is cluster-wide, so an
    /// open transaction of another test's database may hold delivery back for a moment; it never reorders or drops it.
    /// </summary>
    public async Task<(List<WallChange> Rows, FeedCursor Next)> DrainUntilAsync(FeedCursor after, int expected)
    {
        var rows = new List<WallChange>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var (more, next) = await DrainAsync(after);
            rows.AddRange(more);
            after = next;
            if (rows.Count >= expected || DateTime.UtcNow > deadline)
            {
                return (rows, after);
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Reads from <paramref name="after"/> until a short page.</summary>
    public async Task<(List<WallChange> Rows, FeedCursor Next)> DrainAsync(FeedCursor after)
    {
        var rows = new List<WallChange>();
        while (true)
        {
            var page = await Feed.ReadAsync(Filter, after, limit: 100);
            Assert.False(page.Resync);
            rows.AddRange(page.Changes);
            after = page.Next;
            if (page.Changes.Count < 100)
            {
                return (rows, after);
            }
        }
    }
}
