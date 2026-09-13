using System.Security.Cryptography;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Phase A of the change journal: the capture backbone. Exercises the persisting
/// <see cref="ChangeJournalInterceptor"/> over a SQLite-backed context, wired with the same ambient
/// <see cref="ChangeJournal"/> batch tracker it uses in production.
/// </summary>
public sealed class ChangeJournalTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly string connectionString;

    public ChangeJournalTests()
    {
        connectionString = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(connectionString);
        connection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    private ChangeJournal Journal { get; } = new();

    public void Dispose()
    {
        connection.Dispose();
    }

    [Fact]
    public async Task Batch_CapturesInsertUpdateAndDeleteWithKeysAndChangedOnlyAfterImage()
    {
        var wallId = await SeedWallAsync(holdCount: 2);
        var holds = await HoldIdsAsync(wallId);

        Guid newHoldId;
        using (Journal.BeginBatch("test-batch", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = CreateContext();
            var wall = await db.Walls.FirstAsync(w => w.Id == wallId);
            wall.Name = "Renamed Wall";

            var newHold = new Hold { WallId = wallId, X = 0.9, Y = 0.9, Radius = 0.02, Generation = 0 };
            newHoldId = newHold.Id;
            db.Holds.Add(newHold);

            var doomed = await db.Holds.FirstAsync(h => h.Id == holds[0]);
            db.Holds.Remove(doomed);

            await db.SaveChangesAsync();
        }

        await using var check = CreateContext();
        var batch = await check.ChangeJournalBatches.SingleAsync(b => b.Label == "test-batch");
        Assert.Equal(ChangeJournalScopeKind.Wall, batch.ScopeKind);
        Assert.Equal(wallId, batch.ScopeId);
        Assert.Equal(ChangeJournalStatus.Recorded, batch.Status);

        var entries = await check.ChangeJournalEntries
            .Where(e => e.BatchId == batch.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { 0, 1, 2 }, entries.Select(e => e.Seq).ToArray());

        var insert = Assert.Single(entries, e => e.Op == ChangeJournalOp.Insert);
        Assert.Equal(nameof(Hold), insert.EntityType);
        Assert.Contains(newHoldId.ToString(), insert.KeyJson);
        Assert.Null(insert.BeforeJson);
        Assert.NotNull(insert.AfterJson);

        var delete = Assert.Single(entries, e => e.Op == ChangeJournalOp.Delete);
        Assert.Equal(nameof(Hold), delete.EntityType);
        Assert.Contains(holds[0].ToString(), delete.KeyJson);
        Assert.NotNull(delete.BeforeJson);
        Assert.Null(delete.AfterJson);

        var update = Assert.Single(entries, e => e.Op == ChangeJournalOp.Update);
        Assert.Equal(nameof(Wall), update.EntityType);
        Assert.Contains(wallId.ToString(), update.KeyJson);
        // AfterJson holds ONLY the changed property.
        Assert.Contains("\"Name\":\"Renamed Wall\"", update.AfterJson);
        Assert.DoesNotContain("Photo", update.AfterJson);
        Assert.DoesNotContain("OwnerId", update.AfterJson);
        // BeforeJson holds the pre-change value of that same property.
        Assert.Contains("\"Name\":\"Test Wall\"", update.BeforeJson);
    }

    [Fact]
    public async Task NoScope_CreatesAnImplicitAdhocBatchPerSaveChanges()
    {
        var wallId = await SeedWallAsync(holdCount: 0);

        await using (var db = CreateContext())
        {
            var wall = await db.Walls.FirstAsync(w => w.Id == wallId);
            wall.Name = "Ad-hoc rename";
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext();
        Assert.True(await check.ChangeJournalBatches.AnyAsync(b => b.Label == "adhoc"));
    }

    [Fact]
    public async Task Blob_ReferencesByHashAndDedupesIdenticalBytes()
    {
        var photo = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant();

        var wallOne = await SeedWallAsync(holdCount: 0);
        var wallTwo = await SeedWallAsync(holdCount: 0);

        await using (var db = CreateContext())
        {
            var wall = await db.Walls.FirstAsync(w => w.Id == wallOne);
            wall.Photo = photo;
            await db.SaveChangesAsync();
        }

        await using var afterFirst = CreateContext();
        var firstEntry = await afterFirst.ChangeJournalEntries
            .Where(e => e.EntityType == nameof(Wall) && e.Op == ChangeJournalOp.Update)
            .OrderByDescending(e => e.CreatedAt)
            .FirstAsync();
        Assert.Contains("\"$blob\":\"" + sha + "\"", firstEntry.AfterJson);
        Assert.Contains("\"len\":256", firstEntry.AfterJson);
        Assert.Equal(1, await afterFirst.JournalBlobs.CountAsync(b => b.Sha256 == sha));
        var stored = await afterFirst.JournalBlobs.SingleAsync(b => b.Sha256 == sha);
        Assert.Equal(photo, stored.Bytes);
        Assert.Equal(256, stored.Len);

        // A second, byte-identical write must reuse the existing blob, not insert a duplicate.
        await using (var db = CreateContext())
        {
            var wall = await db.Walls.FirstAsync(w => w.Id == wallTwo);
            wall.Photo = photo;
            await db.SaveChangesAsync();
        }

        await using var afterSecond = CreateContext();
        Assert.Equal(1, await afterSecond.JournalBlobs.CountAsync(b => b.Sha256 == sha));
    }

    [Fact]
    public async Task JournalRows_AreNeverThemselvesJournalled()
    {
        var wallId = await SeedWallAsync(holdCount: 1);

        using (Journal.BeginBatch("recursion-check", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = CreateContext();
            var wall = await db.Walls.FirstAsync(w => w.Id == wallId);
            wall.Name = "Touched";
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext();
        var journalTypeNames = new[] { nameof(ChangeJournalBatch), nameof(ChangeJournalEntry), nameof(JournalBlob) };
        var journalledJournalRows = await check.ChangeJournalEntries
            .Where(e => journalTypeNames.Contains(e.EntityType))
            .CountAsync();
        Assert.Equal(0, journalledJournalRows);
    }

    private BlocwerkDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ChangeJournalInterceptor(Journal))
            .Options;

        var db = new SqliteBlocwerkDbContext(options);
        db.CurrentUserId = Guid.Empty;
        return db;
    }

    private async Task<Guid> SeedWallAsync(int holdCount)
    {
        await using var db = CreateContext();
        var owner = new User { Identifier = $"owner-{Guid.NewGuid():N}@test", DisplayName = "Owner" };
        db.Users.Add(owner);

        var wall = new Wall
        {
            Name = "Test Wall",
            OwnerId = owner.Id,
            CurrentGeneration = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = owner.Id, Role = WallRole.Admin });

        for (var i = 0; i < holdCount; i++)
        {
            db.Holds.Add(new Hold { WallId = wall.Id, X = 0.1 * (i + 1), Y = 0.1 * (i + 1), Radius = 0.02, Generation = 0 });
        }

        await db.SaveChangesAsync();
        return wall.Id;
    }

    private async Task<List<Guid>> HoldIdsAsync(Guid wallId)
    {
        await using var db = CreateContext();
        return await db.Holds.Where(h => h.WallId == wallId).OrderBy(h => h.X).Select(h => h.Id).ToListAsync();
    }
}
