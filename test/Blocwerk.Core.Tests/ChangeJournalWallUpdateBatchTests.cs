using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The two-phase reality of a wall update: the "run" (StartAsync) stages the next-generation holds in
/// one SaveChanges and the later "promote" carries them over in a SECOND SaveChanges on a different
/// context. This suite proves both phases record into ONE resumable, sealed <c>"wall-update"</c> batch
/// (so no staging write escapes into a separate adhoc batch) and that the single batch is a
/// self-contained unit: reverting it undoes the staged-hold creations too (no orphans), and replaying it
/// onto a fresh database recreates them. The real <see cref="ChangeJournalInterceptor"/> is wired on
/// every context, exactly as production does.
/// </summary>
public sealed class ChangeJournalWallUpdateBatchTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly string cs;
    private readonly ChangeJournal journal;
    private readonly FuncFactory factory;
    private readonly ICurrentUserService currentUser;
    private readonly User owner = new() { Identifier = "owner@test", DisplayName = "Owner" };

    public ChangeJournalWallUpdateBatchTests()
    {
        cs = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(cs);
        connection.Open();

        // The registry factory closes over the journal field, which is assigned on the next line; the
        // delegate is only ever invoked later (BeginWallUpdateBatch/Seal), so the capture is safe.
        journal = new ChangeJournal(CreateContext);
        factory = new FuncFactory(CreateContext);

        using (var db = CreateContext())
        {
            db.Database.EnsureCreated();
        }

        currentUser = Substitute.For<ICurrentUserService>();
        currentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(owner));
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    [Fact]
    public async Task RunThenPromote_RecordIntoOneSealedWallUpdateBatch_WithContinuousSeq()
    {
        var (wallId, oldHoldId, twinId, _) = await SeedAsync();

        // "Run": stage the next-generation panel + hold in ONE SaveChanges, under the wall-update batch.
        using (journal.BeginWallUpdateBatch(wallId))
        {
            await using var db = CreateContext();
            await StageAsync(db, wallId, twinId);
            await db.SaveChangesAsync();
        }

        // While only the run has happened, the batch is OPEN and already carries the staging inserts.
        await using (var mid = CreateContext())
        {
            var open = await mid.ChangeJournalBatches.SingleAsync(b => b.ScopeId == wallId);
            Assert.Equal(ChangeJournal.WallUpdateBatchLabel, open.Label);
            Assert.Null(open.SealedAt);
            Assert.True(await mid.ChangeJournalEntries.AnyAsync(
                e => e.BatchId == open.Id && e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Insert));
        }

        // "Promote": a SECOND SaveChanges on a different context, resuming the same open batch.
        await Service().PromoteAsync(wallId, Confirm(new CarryoverDecision(oldHoldId, CarryKind.Carried, twinId)));

        await using var check = CreateContext();

        // Exactly ONE batch for the wall, now sealed. (The pre-update seed writes form their own separate
        // adhoc batch — expected — so the check is scoped to the wall's batch, not "no adhoc anywhere".)
        var batch = await check.ChangeJournalBatches.SingleAsync(b => b.ScopeId == wallId);
        Assert.Equal(ChangeJournal.WallUpdateBatchLabel, batch.Label);
        Assert.Equal(ChangeJournalScopeKind.Wall, batch.ScopeKind);
        Assert.NotNull(batch.SealedAt);

        // The staged-hold INSERT did NOT leak into a separate adhoc batch: every entry for the twin is in
        // the one wall-update batch. (Against the old promote-only scoping the staging write lived in an
        // implicit adhoc batch, so this — and the single-batch assertions below — would fail.)
        var twinEntryBatchIds = await check.ChangeJournalEntries
            .Where(e => e.EntityType == nameof(Hold) && e.KeyJson.Contains(twinId.ToString()))
            .Select(e => e.BatchId)
            .Distinct()
            .ToListAsync();
        Assert.Equal(batch.Id, Assert.Single(twinEntryBatchIds));

        var entries = await check.ChangeJournalEntries
            .Where(e => e.BatchId == batch.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync();

        // Seq is continuous across the two calls — one uninterrupted 0..N-1 run, not two batches.
        Assert.Equal(Enumerable.Range(0, entries.Count), entries.Select(e => e.Seq));

        // Both phases are present in the ONE batch: the staged-hold INSERT (run) and the Wall UPDATE that
        // only the promote performs (bumping CurrentGeneration), with staging ordered before promote.
        var stagedHoldInsert = Assert.Single(
            entries, e => e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Insert && e.KeyJson.Contains(twinId.ToString()));
        var wallPromoteUpdate = Assert.Single(
            entries, e => e.EntityType == nameof(Wall) && e.Op == ChangeJournalOp.Update);
        Assert.True(stagedHoldInsert.Seq < wallPromoteUpdate.Seq);
    }

    [Fact]
    public async Task Revert_OfUnifiedBatch_RestoresPreUpdateState_NoOrphanHolds()
    {
        var (wallId, oldHoldId, twinId, boulderId) = await SeedAsync();
        await RunAndPromoteAsync(wallId, oldHoldId, twinId);

        Guid batchId;
        await using (var pre = CreateContext())
        {
            batchId = (await pre.ChangeJournalBatches.SingleAsync(b => b.ScopeId == wallId)).Id;
            // Sanity: post-update the staged twin exists and the wall advanced.
            Assert.True(await pre.Holds.AnyAsync(h => h.Id == twinId));
            Assert.Equal(1, (await pre.Walls.SingleAsync(w => w.Id == wallId)).CurrentGeneration);
        }

        var result = await new ChangeJournalReverter(factory, journal).RevertBatchAsync(batchId);

        Assert.True(result.Reverted);
        Assert.Empty(result.Conflicts);

        await using var check = CreateContext();
        // The staged-hold creation is undone — no orphan left behind by the revert.
        Assert.False(await check.Holds.AnyAsync(h => h.Id == twinId));
        // Only the original old hold remains, at its original generation.
        var holds = await check.Holds.Where(h => h.WallId == wallId).ToListAsync();
        Assert.Equal(oldHoldId, Assert.Single(holds).Id);
        Assert.Equal(0, holds[0].Generation);
        // Wall back at gen 0; the lineage link and the archived reset are gone; boulder points at the old row.
        Assert.Equal(0, (await check.Walls.SingleAsync(w => w.Id == wallId)).CurrentGeneration);
        Assert.False(await check.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == oldHoldId));
        Assert.False(await check.WallResets.AnyAsync(r => r.WallId == wallId));
        Assert.Equal(oldHoldId, (await check.BoulderHolds.SingleAsync(bh => bh.BoulderId == boulderId)).HoldId);
        Assert.Equal(0, (await check.Boulders.SingleAsync(b => b.Id == boulderId)).Generation);
    }

    [Fact]
    public async Task Replay_OfUnifiedBatch_ReachesPostUpdateState_NoConflicts()
    {
        var (wallId, oldHoldId, twinId, boulderId) = await SeedAsync();
        await RunAndPromoteAsync(wallId, oldHoldId, twinId);

        Guid batchId;
        await using (var pre = CreateContext())
        {
            batchId = (await pre.ChangeJournalBatches.SingleAsync(b => b.ScopeId == wallId)).Id;
        }

        var package = await new ChangeJournalExporter(factory).ExportBatchesAsync([batchId]);

        // A SEPARATE database seeded with the PRE-update state (a "prod snapshot" before the update ran).
        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        var targetJournal = new ChangeJournal();
        var targetFactory = new FuncFactory(() => JournalledContext(targetCs, targetJournal));
        using (var db = targetFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await SeedBaseAsync(targetFactory, wallId, oldHoldId, boulderId);

        var report = await new ChangeJournalReplayer(targetFactory, targetJournal).ImportReplayAsync(package, dryRun: false);

        Assert.Empty(report.FatalErrors);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        Assert.True(report.Applied);

        // The staged-hold creation was in the batch, so replay recreated it and reached the post-update state.
        await using var check = targetFactory.CreateDbContext();
        var twin = await check.Holds.SingleAsync(h => h.Id == twinId);
        Assert.Equal(1, twin.Generation);
        Assert.Equal(1, (await check.Walls.SingleAsync(w => w.Id == wallId)).CurrentGeneration);
        Assert.Equal(twinId, (await check.BoulderHolds.SingleAsync(bh => bh.BoulderId == boulderId)).HoldId);
    }

    // ----- helpers -------------------------------------------------------------------------------

    private WallBigUpdateService Service() =>
        new(
            factory,
            currentUser,
            Substitute.For<IHoldDetectionService>(),
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance,
            journal);

    private async Task RunAndPromoteAsync(Guid wallId, Guid oldHoldId, Guid twinId)
    {
        using (journal.BeginWallUpdateBatch(wallId))
        {
            await using var db = CreateContext();
            await StageAsync(db, wallId, twinId);
            await db.SaveChangesAsync();
        }

        await Service().PromoteAsync(wallId, Confirm(new CarryoverDecision(oldHoldId, CarryKind.Carried, twinId)));
    }

    // Stages the centre panel (0,0) at gen 1 with one staged detection that is the old hold's twin. The
    // twin is inserted with NeedsReview=false so the in-place promote leaves it byte-identical (a matched
    // Carried twin is set NeedsReview=false), keeping it a single-write row for a clean revert/replay.
    private static async Task StageAsync(BlocwerkDbContext db, Guid wallId, Guid twinId)
    {
        var panel = new WallPanel
        {
            WallId = wallId,
            Col = 0,
            Row = 0,
            Photo = null,
            StagedPhoto = [4, 5, 6],
            StagedPhotoContentType = "image/jpeg",
            Generation = 1,
        };
        db.WallPanels.Add(panel);
        db.Holds.Add(new Hold
        {
            Id = twinId,
            WallId = wallId,
            WallPanelId = panel.Id,
            X = 0.5,
            Y = 0.5,
            Radius = 0.02,
            Generation = 1,
            IsAutoDetected = true,
            NeedsReview = false,
        });
    }

    private async Task<(Guid WallId, Guid OldHoldId, Guid TwinId, Guid BoulderId)> SeedAsync()
    {
        var wallId = Guid.NewGuid();
        var oldHoldId = Guid.NewGuid();
        var boulderId = Guid.NewGuid();
        await SeedBaseAsync(factory, wallId, oldHoldId, boulderId);
        return (wallId, oldHoldId, Guid.NewGuid(), boulderId);
    }

    // Seeds the shared pre-update baseline: owner + admin membership, a wall with a live photo at gen 0,
    // one old hold at gen 0, and a boulder that uses it. Used for both the source and the replay target.
    private async Task SeedBaseAsync(IDbContextFactory<BlocwerkDbContext> f, Guid wallId, Guid oldHoldId, Guid boulderId)
    {
        await using var db = f.CreateDbContext();
        db.CurrentUserId = Guid.Empty;
        if (!await db.Users.AnyAsync(u => u.Id == owner.Id))
        {
            db.Users.Add(new User { Id = owner.Id, Identifier = owner.Identifier, DisplayName = owner.DisplayName });
        }

        db.Walls.Add(new Wall
        {
            Id = wallId,
            Name = "Test Wall",
            OwnerId = owner.Id,
            CurrentGeneration = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        });
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = owner.Id, Role = WallRole.Admin });
        db.Holds.Add(new Hold { Id = oldHoldId, WallId = wallId, X = 0.1, Y = 0.1, Radius = 0.02, Generation = 0 });
        db.Boulders.Add(new Boulder
        {
            Id = boulderId,
            WallId = wallId,
            Name = "Boulder",
            CreatedByUserId = owner.Id,
            Generation = 0,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulderId, HoldId = oldHoldId });
        await db.SaveChangesAsync();
    }

    private static BigUpdateConfirmation Confirm(params CarryoverDecision[] carryover) =>
        new([.. carryover], [], [], []);

    private BlocwerkDbContext CreateContext() => JournalledContext(cs, journal);

    private static BlocwerkDbContext JournalledContext(string connectionString, ChangeJournal journal)
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ChangeJournalInterceptor(journal))
            .Options;
        var db = new SqliteBlocwerkDbContext(options);
        db.CurrentUserId = Guid.Empty;
        return db;
    }

    /// <summary>An <see cref="IDbContextFactory{TContext}"/> over a delegate, so every context carries the capture interceptor.</summary>
    private sealed class FuncFactory : IDbContextFactory<BlocwerkDbContext>
    {
        private readonly Func<BlocwerkDbContext> create;

        public FuncFactory(Func<BlocwerkDbContext> create)
        {
            this.create = create;
        }

        public BlocwerkDbContext CreateDbContext() => create();
    }
}
