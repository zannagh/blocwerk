using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The operator-facing half of the change journal: the read-only <see cref="ChangeJournalBrowser"/>
/// listing, the write-free <see cref="ChangeJournalReverter.PreviewRevertAsync"/>, the conditional
/// claim that makes a double or concurrent revert a clean result instead of a second inverse, and the
/// acting user being recorded on the revert's own batch.
/// </summary>
public sealed class ChangeJournalOperatorRevertTests : IDisposable
{
    private readonly SqliteConnection keepAlive;
    private readonly string cs;
    private readonly ChangeJournal journal = new();

    public ChangeJournalOperatorRevertTests()
    {
        cs = TestDbContextFactory.IsolatedDatabase();
        keepAlive = new SqliteConnection(cs);
        keepAlive.Open();
        using var db = Context();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        keepAlive.Dispose();
    }

    [Fact]
    public async Task Browser_ResolvesScopeActorAndGroupedCounts()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        await using (var db = Context(userId))
        {
            using var _ = journal.BeginBatch("hold-clean-outside-border", ChangeJournalScopeKind.Wall, wallId);
            db.Holds.RemoveRange(await db.Holds.Where(h => h.WallId == wallId).ToListAsync());
            db.Holds.Add(new Hold { WallId = wallId, X = 0.5, Y = 0.5, Radius = 0.02, Generation = 0 });
            await db.SaveChangesAsync();
        }

        // The seed's own writes are journalled too, as an implicit "adhoc" batch; pick out the named one.
        var page = await Browser().ListRecentAsync();
        var summary = page.Items.Single(b => b.Label == "hold-clean-outside-border");

        Assert.Equal("hold-clean-outside-border", summary.Label);
        Assert.Equal("Nordwand", summary.ScopeName);
        Assert.Equal(userId.ToString(), summary.ActorUserId);
        Assert.Equal("Patrick", summary.ActorName);
        Assert.Equal(4, summary.EntryCount);
        Assert.Equal(3, summary.Counts.Single(c => c.Op == ChangeJournalOp.Delete && c.EntityType == "Hold").Count);
        Assert.Equal(1, summary.Counts.Single(c => c.Op == ChangeJournalOp.Insert && c.EntityType == "Hold").Count);
        Assert.False(summary.IsWallUpdateBatch);
    }

    [Fact]
    public async Task Browser_ReportsUnattributedActorAsNull()
    {
        var (wallId, _) = await SeedWallAsync("Nordwand", "Patrick");
        await RenameAsync(wallId, "Renamed", "edit", actor: null);

        var summary = (await Browser().ListRecentAsync()).Items.Single(b => b.Label == "edit");
        Assert.Null(summary.ActorUserId);
        Assert.Null(summary.ActorName);
    }

    [Fact]
    public async Task Browser_CountsNewerBatchesOnTheSameScopeAndPages()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        var (otherWallId, _) = await SeedWallAsync("Südwand", "Other");
        await RenameAsync(wallId, "First", "older", userId);
        await RenameAsync(wallId, "Second", "newer", userId);
        await RenameAsync(otherWallId, "Elsewhere", "unrelated", userId);

        var all = await Browser().ListRecentAsync();
        Assert.Equal(1, all.Items.Single(b => b.Label == "older").NewerBatchesOnScope);
        Assert.Equal(0, all.Items.Single(b => b.Label == "newer").NewerBatchesOnScope);
        Assert.Equal(0, all.Items.Single(b => b.Label == "unrelated").NewerBatchesOnScope);

        var scoped = await Browser().ListForScopeAsync(ChangeJournalScopeKind.Wall, wallId);
        Assert.Equal(2, scoped.Items.Count);
        Assert.DoesNotContain(scoped.Items, b => b.Label == "unrelated");

        // Five batches in total: the three named ones plus an implicit adhoc batch per seeded wall.
        var firstPage = await Browser().ListRecentAsync(skip: 0, take: 2);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);

        var lastPage = await Browser().ListRecentAsync(skip: 3, take: 2);
        Assert.Equal(2, lastPage.Items.Count);
        Assert.False(lastPage.HasMore);
    }

    [Fact]
    public async Task Browser_FlagsWallUpdateBatchAndPreviewNamesTheOrphanedSession()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        Guid sessionId;
        await using (var db = Context(userId))
        {
            var session = new WallUpdateSession { WallId = wallId, StagedGeneration = 1, Status = WallUpdateSessionStatus.Open };
            sessionId = session.Id;
            db.WallUpdateSessions.Add(session);
            await db.SaveChangesAsync();
        }

        await RenameAsync(wallId, "Promoted", ChangeJournal.WallUpdateBatchLabel, userId);

        var summary = (await Browser().ListRecentAsync()).Items
            .Single(b => b.Label == ChangeJournal.WallUpdateBatchLabel);
        Assert.True(summary.IsWallUpdateBatch);

        var preview = await Reverter().PreviewRevertAsync(summary.BatchId);
        Assert.True(preview.RequiresWallUpdateSessionCleanup);
        Assert.Equal(sessionId, preview.WallUpdateSessionId);
    }

    [Fact]
    public async Task Preview_FindsConflictsResolvesTheirKeysAndWritesNothing()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        await RenameAsync(wallId, "Renamed", "edit", userId);
        var batchId = await BatchIdAsync("edit");

        // Out-of-band edit after capture: the current name no longer matches the recorded after-image.
        await using (var db = Context(userId))
        {
            (await db.Walls.FirstAsync(w => w.Id == wallId)).Name = "Something else";
            await db.SaveChangesAsync();
        }

        var before = await JournalShapeAsync();
        var preview = await Reverter().PreviewRevertAsync(batchId);

        Assert.True(preview.Found);
        Assert.False(preview.CanRevert);
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal("Wall", conflict.Conflict.EntityType);
        Assert.Equal("Something else", conflict.EntityName);
        Assert.Contains($"Id={wallId:N}"[..11], conflict.ShortKey);
        Assert.DoesNotContain("{", conflict.Description);

        // The preview opened no transaction and saved nothing: journal and row state are untouched.
        Assert.Equal(before, await JournalShapeAsync());
        await using var check = Context(userId);
        Assert.Equal("Something else", (await check.Walls.FirstAsync(w => w.Id == wallId)).Name);
        Assert.Equal(ChangeJournalStatus.Recorded, (await check.ChangeJournalBatches.FirstAsync(b => b.Id == batchId)).Status);
    }

    [Fact]
    public async Task Preview_OfACleanBatchSaysItCanRevert()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        await RenameAsync(wallId, "Renamed", "edit", userId);

        var preview = await Reverter().PreviewRevertAsync(await BatchIdAsync("edit"));
        Assert.True(preview.CanRevert);
        Assert.Empty(preview.Conflicts);
        Assert.Equal(1, preview.EntryCount);
        Assert.False(preview.RequiresWallUpdateSessionCleanup);
    }

    [Fact]
    public async Task Revert_RecordsTheActingUserOnItsOwnBatch()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        await RenameAsync(wallId, "Renamed", "edit", userId);

        var result = await Reverter().RevertBatchAsync(await BatchIdAsync("edit"), actingUserId: userId);
        Assert.True(result.Reverted);
        Assert.Equal(ChangeJournalRevertOutcome.Reverted, result.Outcome);

        await using var check = Context(userId);
        var revertBatch = await check.ChangeJournalBatches.FirstAsync(b => b.Id == result.RevertBatchId);
        Assert.Equal(userId.ToString(), revertBatch.Actor);

        var summary = await Browser().GetBatchAsync(revertBatch.Id);
        Assert.Equal("Patrick", summary!.ActorName);
    }

    [Fact]
    public async Task Revert_SecondAttemptLosesTheClaimAndAppliesNothing()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        await RenameAsync(wallId, "Renamed", "edit", userId);
        var batchId = await BatchIdAsync("edit");

        Assert.True((await Reverter().RevertBatchAsync(batchId, actingUserId: userId)).Reverted);
        var after = await JournalShapeAsync();

        var second = await Reverter().RevertBatchAsync(batchId, actingUserId: userId);

        Assert.False(second.Reverted);
        Assert.Equal(ChangeJournalRevertOutcome.AlreadyReverted, second.Outcome);
        Assert.Empty(second.Conflicts);
        Assert.Null(second.RevertBatchId);

        // No second inverse: no extra revert batch, no extra entries, and the wall still reads as restored.
        Assert.Equal(after, await JournalShapeAsync());
        await using var check = Context(userId);
        Assert.Equal("Nordwand", (await check.Walls.FirstAsync(w => w.Id == wallId)).Name);
    }

    [Fact]
    public async Task Revert_ConcurrentAttemptsApplyTheInverseExactlyOnce()
    {
        var (wallId, userId) = await SeedWallAsync("Nordwand", "Patrick");
        await RenameAsync(wallId, "Renamed", "edit", userId);
        var batchId = await BatchIdAsync("edit");

        // Two operators racing the same batch. The loser may also surface as a SQLite locking failure —
        // that is the in-memory harness, not the claim; what matters is that exactly one revert applies.
        var attempts = await Task.WhenAll(
            AttemptRevertAsync(batchId, userId),
            AttemptRevertAsync(batchId, userId));

        Assert.Single(attempts.Where(r => r is { Reverted: true }));
        Assert.DoesNotContain(attempts, r => r is { Reverted: false, Outcome: ChangeJournalRevertOutcome.Reverted });

        await using var check = Context(userId);
        Assert.Equal("Nordwand", (await check.Walls.FirstAsync(w => w.Id == wallId)).Name);
        Assert.Equal(1, await check.ChangeJournalBatches.CountAsync(b => b.Label == "revert:edit"));
    }

    private async Task<ChangeJournalRevertResult?> AttemptRevertAsync(Guid batchId, Guid userId)
    {
        try
        {
            return await Task.Run(() => Reverter().RevertBatchAsync(batchId, actingUserId: userId));
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (DbUpdateException)
        {
            return null;
        }
    }

    /// <summary>A cheap fingerprint of the whole journal, to assert an operation wrote nothing.</summary>
    private async Task<string> JournalShapeAsync()
    {
        await using var db = Context();
        var batches = await db.ChangeJournalBatches.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Select(b => $"{b.Id}:{b.Label}:{b.Status}").ToListAsync();
        var entries = await db.ChangeJournalEntries.CountAsync();
        return string.Join("|", batches) + $"#{entries}";
    }

    private async Task<(Guid WallId, Guid UserId)> SeedWallAsync(string name, string owner)
    {
        var wallId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var db = Context();
        db.Users.Add(new User { Id = userId, Identifier = $"{owner}-{userId:N}@test", DisplayName = owner });
        db.Walls.Add(new Wall
        {
            Id = wallId,
            Name = name,
            OwnerId = userId,
            CurrentGeneration = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        });
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = userId, Role = WallRole.Admin });
        db.Holds.Add(new Hold { WallId = wallId, X = 0.1, Y = 0.1, Radius = 0.02, Generation = 0 });
        db.Holds.Add(new Hold { WallId = wallId, X = 0.2, Y = 0.2, Radius = 0.02, Generation = 0 });
        db.Holds.Add(new Hold { WallId = wallId, X = 0.3, Y = 0.3, Radius = 0.02, Generation = 0 });
        await db.SaveChangesAsync();
        return (wallId, userId);
    }

    private async Task RenameAsync(Guid wallId, string name, string label, Guid? actor)
    {
        await using var db = Context(actor);
        using var _ = journal.BeginBatch(label, ChangeJournalScopeKind.Wall, wallId);
        (await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == wallId)).Name = name;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> BatchIdAsync(string label)
    {
        await using var db = Context();
        return (await db.ChangeJournalBatches.FirstAsync(b => b.Label == label)).Id;
    }

    private ChangeJournalReverter Reverter() => new(new JournalingFactory(cs, journal, null), journal);

    private ChangeJournalBrowser Browser() => new(new JournalingFactory(cs, journal, null));

    private BlocwerkDbContext Context(Guid? actor = null) => new JournalingFactory(cs, journal, actor).CreateDbContext();

    /// <summary>A SQLite context factory that attaches the capture interceptor, as production does.</summary>
    private sealed class JournalingFactory : IDbContextFactory<BlocwerkDbContext>
    {
        private readonly string connectionString;
        private readonly ChangeJournal journal;
        private readonly Guid? actor;

        public JournalingFactory(string connectionString, ChangeJournal journal, Guid? actor)
        {
            this.connectionString = connectionString;
            this.journal = journal;
            this.actor = actor;
        }

        public BlocwerkDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new ChangeJournalInterceptor(journal))
                .Options;
            var db = new SqliteBlocwerkDbContext(options);
            db.CurrentUserId = actor ?? Guid.Empty;
            return db;
        }
    }
}
