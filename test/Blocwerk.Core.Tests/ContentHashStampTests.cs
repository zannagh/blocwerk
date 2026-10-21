// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The save-time content hash (<c>ContentHashStamp</c>): an added row gets the hash of its bytes, replacing the bytes
/// moves it, clearing them clears it, any other edit leaves it, and the change journal never records it. On Postgres the
/// stored hash also equals what <c>sha256(bytea)</c> computes, which the SQL backfill relies on.
/// </summary>
public class ContentHashStampTests
{
    private static readonly byte[] First = [1, 2, 3, 4, 5];
    private static readonly byte[] Second = [9, 8, 7];

    [Fact]
    public Task AnAddedRow_IsHashed_AndTheHashFollowsItsBytes() => FollowsBytesAsync(new WallTestHarness());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AnAddedRow_IsHashed_AndTheHashFollowsItsBytes_OnPostgres() =>
        FollowsBytesAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task TheStoredHash_IsWhatPostgresComputes()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(holdCount: 0);
        await using (var db = h.CreateContext())
        {
            db.WallPanels.Add(new WallPanel { WallId = h.WallId, Photo = First, StagedPhoto = Second });
            await db.SaveChangesAsync();
        }

        await using var check = h.CreateContext();
        var mismatches = await check.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::int AS "Value" FROM "WallPanels"
            WHERE "PhotoSha256" IS DISTINCT FROM sha256("Photo") OR "StagedPhotoSha256" IS DISTINCT FROM sha256("StagedPhoto")
            """).SingleAsync();
        Assert.Equal(0, mismatches);
        Assert.Equal(1, await check.WallPanels.CountAsync());
    }

    [Fact]
    public async Task EveryStampedColumn_IsHashedOnInsert()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var seeded = await ContentHashSeed.EveryColumnAsync(h, First);

        await using var db = h.CreateContext();
        foreach (var (column, hash) in await ContentHashSeed.ReadHashesAsync(db, seeded))
        {
            Assert.True(hash is not null && SHA256.HashData(First).AsSpan().SequenceEqual(hash), $"{column.Entity.Name}.{column.Hash}");
        }
    }

    [Fact]
    public async Task TheJournal_RecordsTheBytes_NotTheirHash()
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        keepAlive.Open();
        var factory = new JournallingDbContextFactory(connectionString, new ChangeJournal());
        await using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var owner = new User { Identifier = "owner@test" };
            db.Users.Add(owner);
            db.Walls.Add(new Wall { Name = "Journalled", OwnerId = owner.Id, Photo = First });
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateDbContext())
        {
            (await db.Walls.IgnoreQueryFilters().SingleAsync()).Photo = Second;
            await db.SaveChangesAsync();
        }

        await using var check = factory.CreateDbContext();
        var walls = await check.ChangeJournalEntries.Where(e => e.EntityType == nameof(Wall)).ToListAsync();
        Assert.Equal(2, walls.Count);
        Assert.All(walls, e => Assert.DoesNotContain("Sha256", (e.BeforeJson ?? string.Empty) + e.AfterJson));
        Assert.All(walls, e => Assert.Contains("\"Photo\"", e.AfterJson));
        Assert.Equal(2, await check.JournalBlobs.CountAsync());
        var stored = await check.Walls.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(SHA256.HashData(Second), stored.PhotoSha256);
    }

    private static async Task FollowsBytesAsync(WallTestHarness harness)
    {
        using var h = harness;
        await h.SeedWallAsync(holdCount: 0);
        Guid id;
        await using (var db = h.CreateContext())
        {
            var panel = new WallPanel { WallId = h.WallId, Photo = First };
            db.WallPanels.Add(panel);
            await db.SaveChangesAsync();
            id = panel.Id;
        }

        await AssertHashesAsync(h, id, First, null);

        await EditAsync(h, id, p => p.Col = 3);
        await AssertHashesAsync(h, id, First, null);

        await EditAsync(h, id, p => (p.Photo, p.StagedPhoto) = (Second, First));
        await AssertHashesAsync(h, id, Second, First);

        // A hash written by hand, the bytes untouched, is put back: the column only ever holds the bytes' hash.
        await EditAsync(h, id, p => p.PhotoSha256 = new byte[32]);
        await AssertHashesAsync(h, id, Second, First);

        await EditAsync(h, id, p => p.StagedPhoto = null);
        await AssertHashesAsync(h, id, Second, null);
    }

    private static async Task EditAsync(WallTestHarness h, Guid id, Action<WallPanel> edit)
    {
        await using var db = h.CreateContext();
        edit(await db.WallPanels.SingleAsync(p => p.Id == id));
        await db.SaveChangesAsync();
    }

    private static async Task AssertHashesAsync(WallTestHarness h, Guid id, byte[]? photo, byte[]? staged)
    {
        await using var db = h.CreateContext();
        var row = await db.WallPanels.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(photo is null ? null : SHA256.HashData(photo), row.PhotoSha256);
        Assert.Equal(staged is null ? null : SHA256.HashData(staged), row.StagedPhotoSha256);
    }
}
