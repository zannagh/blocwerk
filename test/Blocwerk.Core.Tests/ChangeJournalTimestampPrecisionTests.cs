// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A journal image recorded on a Linux host carries 100 ns timestamps (the in-memory value), while PostgreSQL stores the
/// row to the microsecond. Re-sending such a package, or reverting it, must not read those sub-microsecond differences
/// as conflicts. The SQLite variant stores the rows at microseconds by hand; the Postgres variant lets the server do it.
/// </summary>
public sealed class ChangeJournalTimestampPrecisionTests
{
    // .1234567 s: PostgreSQL rounds it UP to .123457 (truncation would give .123456).
    private static readonly DateTimeOffset RoundsUp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

    // .7654321 s: rounds down to .765432 (Npgsql only writes UTC offsets to timestamptz).
    private static readonly DateTimeOffset RoundsDown = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddTicks(7_654_321);

    [Fact]
    public async Task Sqlite_ReSendAndRevert_OfAPackageWith100nsImages_AgainstRowsStoredAtMicroseconds_DoNotConflict()
    {
        using var source = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        source.Open();
        target.Open();
        await RunAsync(source.ConnectionString, target.ConnectionString, serverRounds: false);
    }

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task Postgres_ReSendAndRevert_OfAPackageWith100nsImages_AgainstRowsStoredAtMicroseconds_DoNotConflict()
    {
        using var source = PostgresTestDatabase.Create();
        using var target = PostgresTestDatabase.Create();
        await RunAsync(source.ConnectionString, target.ConnectionString, serverRounds: true);
    }

    [Fact]
    public void CurrentMatches_DateTimeAndTimeSpan_EqualWithinAMicrosecond_DifferentFromOne()
    {
        using var db = TestDb.Create(TestDbContextFactory.IsolatedDatabase());
        var at = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        var session = new HangboardSession { Duration = TimeSpan.FromTicks(98_765_437), Timestamp = RoundsUp };
        var subscription = new PushSubscription { Endpoint = "e", P256dh = "p", Auth = "a", CreatedAtUtc = at };
        var sessionEntry = db.Attach(session);
        var subscriptionEntry = db.Attach(subscription);

        Assert.True(Matches(sessionEntry, nameof(HangboardSession.Duration), TimeSpan.FromTicks(98_765_440)));
        Assert.True(Matches(sessionEntry, nameof(HangboardSession.Timestamp), Micro(RoundsUp)));
        Assert.True(Matches(sessionEntry, nameof(HangboardSession.Timestamp), Micro(RoundsUp).ToOffset(TimeSpan.FromHours(2))));
        Assert.True(Matches(subscriptionEntry, nameof(PushSubscription.CreatedAtUtc), at.AddTicks(3)));
        Assert.True(Matches(subscriptionEntry, nameof(PushSubscription.CreatedAtUtc), at.AddTicks(-7)));

        Assert.False(Matches(sessionEntry, nameof(HangboardSession.Duration), TimeSpan.FromTicks(98_765_447)));
        Assert.False(Matches(sessionEntry, nameof(HangboardSession.Timestamp), RoundsUp.AddTicks(10)));
        Assert.False(Matches(subscriptionEntry, nameof(PushSubscription.CreatedAtUtc), at.AddTicks(-10)));
    }

    private static bool Matches(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string property, object value) =>
        ChangeJournalEntityAccessor.CurrentMatches(entry, new Dictionary<string, object?> { [property] = value }, out _);

    private static async Task RunAsync(string sourceCs, string targetCs, bool serverRounds)
    {
        var journal = new ChangeJournal();
        var src = new Factory(sourceCs, journal);
        var tgt = new Factory(targetCs, journal);
        var (wallId, ownerId) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedAsync(sourceCs, wallId, ownerId);
        await SeedAsync(targetCs, wallId, ownerId);

        // Insert, then (same context, so the before-image is the in-memory 100 ns value) rename: two batches.
        var boulder = new Boulder
        {
            WallId = wallId, Name = "Boulder", CreatedByUserId = ownerId, CreatedAt = RoundsUp, PublishedAt = RoundsDown,
        };
        await using (var db = src.CreateDbContext())
        {
            using (journal.BeginBatch("insert", ChangeJournalScopeKind.Wall, wallId))
            {
                db.Boulders.Add(boulder);
                await db.SaveChangesAsync();
            }

            using (journal.BeginBatch("rename", ChangeJournalScopeKind.Wall, wallId))
            {
                boulder.Name = "Renamed";
                await db.SaveChangesAsync();
            }
        }

        await StoreAtMicrosecondsAsync(sourceCs, boulder.Id, serverRounds);
        var insertBatch = await BatchIdAsync(sourceCs, "insert");
        var renameBatch = await BatchIdAsync(sourceCs, "rename");
        var package = await new ChangeJournalExporter(src).ExportBatchesAsync([insertBatch, renameBatch]);

        // Send, then re-send: the second finds every row already holding the net effect.
        var sent = await new ChangeJournalReplayer(tgt, journal).ImportReplayAsync(package, dryRun: false);
        Assert.True(sent.Applied, string.Join("; ", sent.FatalErrors));
        Assert.DoesNotContain(sent.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await StoreAtMicrosecondsAsync(targetCs, boulder.Id, serverRounds);

        var resent = await new ChangeJournalReplayer(tgt, journal).ImportReplayAsync(package, dryRun: false);
        Assert.Empty(resent.FatalErrors);
        Assert.NotEmpty(resent.Rows);
        Assert.All(resent.Rows, r => Assert.Equal(ChangeJournalReplayOutcome.SkippedAlreadyApplied, r.Outcome));

        // Revert the replay on the target, and the two original batches (newest first) on the source.
        var targetRevert = await new ChangeJournalReverter(tgt, journal).RevertBatchAsync(sent.ReplayBatchId!.Value);
        Assert.Empty(targetRevert.Conflicts);
        Assert.True(targetRevert.Reverted);

        var renameRevert = await new ChangeJournalReverter(src, journal).RevertBatchAsync(renameBatch);
        Assert.Empty(renameRevert.Conflicts);
        Assert.True(renameRevert.Reverted);
        var insertRevert = await new ChangeJournalReverter(src, journal).RevertBatchAsync(insertBatch);
        Assert.Empty(insertRevert.Conflicts);
        Assert.True(insertRevert.Reverted);

        await using var checkSource = TestDb.Create(sourceCs);
        await using var checkTarget = TestDb.Create(targetCs);
        Assert.False(await checkSource.Boulders.AnyAsync(b => b.Id == boulder.Id));
        Assert.False(await checkTarget.Boulders.AnyAsync(b => b.Id == boulder.Id));
    }

    /// <summary>
    /// Brings the row to the microsecond the way each kind of write does. Npgsql truncates the 7th digit of a parameter
    /// on the client; PostgreSQL itself rounds a literal half-up, so CreatedAt is then set from text to get the
    /// round-UP case (.1234567 -> .123457) on the server too. SQLite keeps all 100 ns digits, so there the row is set to
    /// the rounded values by hand. Either way, out of the journal.
    /// </summary>
    private static async Task StoreAtMicrosecondsAsync(string cs, Guid boulderId, bool serverRounds)
    {
        await using var db = TestDb.Create(cs);
        if (serverRounds)
        {
            var written = await db.Boulders.SingleAsync(b => b.Id == boulderId);
            Assert.Equal(0, written.CreatedAt.UtcTicks % 10);
            Assert.NotEqual(RoundsUp.UtcTicks, written.CreatedAt.UtcTicks);
            Assert.Equal(Micro(RoundsDown).UtcTicks, written.PublishedAt!.Value.UtcTicks);

            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Boulders\" SET \"CreatedAt\" = '2026-10-20 12:00:00.1234567+00' WHERE \"Id\" = {0}", boulderId);
            var rounded = await db.Boulders.AsNoTracking().SingleAsync(b => b.Id == boulderId);
            Assert.Equal(Micro(RoundsUp).UtcTicks, rounded.CreatedAt.UtcTicks);
            Assert.True(rounded.CreatedAt > RoundsUp);
            return;
        }

        var row = await db.Boulders.SingleAsync(b => b.Id == boulderId);
        row.CreatedAt = Micro(RoundsUp);
        row.PublishedAt = Micro(RoundsDown);
        await db.SaveChangesAsync();
    }

    /// <summary>The value rounded half-up to the microsecond, in UTC, as PostgreSQL stores it.</summary>
    private static DateTimeOffset Micro(DateTimeOffset value) =>
        new((value.UtcTicks + 5) / 10 * 10, TimeSpan.Zero);

    private static async Task SeedAsync(string cs, Guid wallId, Guid ownerId)
    {
        await using var db = TestDb.Create(cs);
        if (!TestDb.IsPostgres(cs))
        {
            await db.Database.EnsureCreatedAsync();
        }

        db.Users.Add(new User { Id = ownerId, Identifier = $"owner-{ownerId:N}@test", DisplayName = "Owner" });
        db.Walls.Add(new Wall
        {
            Id = wallId, Name = "Test Wall", OwnerId = ownerId, CurrentGeneration = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg",
        });
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = ownerId, Role = WallRole.Admin });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> BatchIdAsync(string cs, string label)
    {
        await using var db = TestDb.Create(cs);
        return await db.ChangeJournalBatches.Where(b => b.Label == label).Select(b => b.Id).SingleAsync();
    }

    /// <summary>A context factory for either provider with the capture interceptor, so replay/revert writes journal too.</summary>
    private sealed class Factory(string connectionString, ChangeJournal journal) : IDbContextFactory<BlocwerkDbContext>
    {
        public BlocwerkDbContext CreateDbContext() => TestDb.Create(connectionString, new ChangeJournalInterceptor(journal));
    }
}
