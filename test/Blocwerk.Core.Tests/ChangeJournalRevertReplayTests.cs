using System.Security.Cryptography;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Phase B (revert) and Phase C (export + replay) of the change journal. Every context carries the
/// same capture <see cref="ChangeJournalInterceptor"/>, so the revert's and replay's own writes are
/// themselves journalled — which these tests assert.
/// </summary>
public sealed class ChangeJournalRevertReplayTests : IDisposable
{
    private readonly SqliteConnection source;
    private readonly string sourceCs;
    private readonly ChangeJournal journal = new();

    public ChangeJournalRevertReplayTests()
    {
        sourceCs = TestDbContextFactory.IsolatedDatabase();
        source = new SqliteConnection(sourceCs);
        source.Open();
        using var db = Context(sourceCs);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        source.Dispose();
    }

    [Fact]
    public async Task Revert_RestoresPreChangeStateAndIsItselfJournalled()
    {
        var wallId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var keepHold = Guid.NewGuid();
        var doomedHold = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, owner, "Test Wall", [1, 2, 3], (keepHold, 0.1), (doomedHold, 0.2));

        Guid addedHold;
        using (journal.BeginBatch("edit", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            var wall = await db.Walls.FirstAsync(w => w.Id == wallId);
            wall.Name = "Renamed";
            var added = new Hold { WallId = wallId, X = 0.9, Y = 0.9, Radius = 0.02, Generation = 0 };
            addedHold = added.Id;
            db.Holds.Add(added);
            db.Holds.Remove(await db.Holds.FirstAsync(h => h.Id == doomedHold));
            await db.SaveChangesAsync();
        }

        var batchId = await BatchIdAsync(sourceCs, "edit");
        var result = await Reverter().RevertBatchAsync(batchId);

        Assert.True(result.Reverted);
        Assert.Empty(result.Conflicts);
        Assert.NotNull(result.RevertBatchId);

        await using var check = Context(sourceCs);
        var wallAfter = await check.Walls.FirstAsync(w => w.Id == wallId);
        Assert.Equal("Test Wall", wallAfter.Name);
        Assert.False(await check.Holds.AnyAsync(h => h.Id == addedHold));
        Assert.True(await check.Holds.AnyAsync(h => h.Id == doomedHold));

        var original = await check.ChangeJournalBatches.FirstAsync(b => b.Id == batchId);
        Assert.Equal(ChangeJournalStatus.Reverted, original.Status);
        Assert.True(await check.ChangeJournalBatches.AnyAsync(b => b.Id == result.RevertBatchId && b.Label == "revert:edit"));
    }

    [Fact]
    public async Task Revert_AbortsWithConflictWhenRowMutatedOutOfBand()
    {
        var wallId = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);

        using (journal.BeginBatch("edit", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            (await db.Walls.FirstAsync(w => w.Id == wallId)).Name = "Renamed";
            await db.SaveChangesAsync();
        }

        // Out-of-band mutation after capture: the current name no longer matches what we recorded.
        await using (var db = Context(sourceCs))
        {
            (await db.Walls.FirstAsync(w => w.Id == wallId)).Name = "Meddled";
            await db.SaveChangesAsync();
        }

        var batchId = await BatchIdAsync(sourceCs, "edit");
        var result = await Reverter().RevertBatchAsync(batchId);

        Assert.False(result.Reverted);
        Assert.NotEmpty(result.Conflicts);

        await using var check = Context(sourceCs);
        Assert.Equal("Meddled", (await check.Walls.FirstAsync(w => w.Id == wallId)).Name);
        Assert.Equal(ChangeJournalStatus.Recorded, (await check.ChangeJournalBatches.FirstAsync(b => b.Id == batchId)).Status);
    }

    [Fact]
    public async Task Revert_RoundTripsABlobColumn()
    {
        var wallId = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);
        var newPhoto = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        using (journal.BeginBatch("photo", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            (await db.Walls.FirstAsync(w => w.Id == wallId)).Photo = newPhoto;
            await db.SaveChangesAsync();
        }

        var result = await Reverter().RevertBatchAsync(await BatchIdAsync(sourceCs, "photo"));
        Assert.True(result.Reverted);

        await using var check = Context(sourceCs);
        Assert.Equal([1, 2, 3], (await check.Walls.FirstAsync(w => w.Id == wallId)).Photo);
    }

    [Fact]
    public async Task Replay_AppliesAfterStateOntoASeparateDatabase()
    {
        var (wallId, addedHold, doomedHold, keepHold, package) = await CaptureAndExportAsync();

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await InitTargetAsync(targetCs, wallId, "Test Wall", keepHold, doomedHold);

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        Assert.NotNull(report.ReplayBatchId);

        await using var check = Context(targetCs);
        Assert.Equal("Renamed", (await check.Walls.FirstAsync(w => w.Id == wallId)).Name);
        Assert.True(await check.Holds.AnyAsync(h => h.Id == addedHold));
        Assert.False(await check.Holds.AnyAsync(h => h.Id == doomedHold));
        Assert.True(await check.ChangeJournalBatches.AnyAsync(b => b.Label == "replay"));
    }

    [Fact]
    public async Task Replay_DryRunReportsButWritesNothing()
    {
        var (wallId, addedHold, _, keepHold, package) = await CaptureAndExportAsync();

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await InitTargetAsync(targetCs, wallId, "Test Wall", keepHold, Guid.NewGuid());

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: true);

        Assert.False(report.Applied);
        Assert.True(report.DryRun);
        Assert.Contains(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Applied);

        await using var check = Context(targetCs);
        Assert.Equal("Test Wall", (await check.Walls.FirstAsync(w => w.Id == wallId)).Name);
        Assert.False(await check.Holds.AnyAsync(h => h.Id == addedHold));
    }

    [Fact]
    public async Task Replay_ConflictsAndWritesNothingWhenTargetDiverged()
    {
        var (wallId, addedHold, _, keepHold, package) = await CaptureAndExportAsync();

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        // Target wall name differs from the recorded before-image, so the Update entry conflicts.
        await InitTargetAsync(targetCs, wallId, "Diverged", keepHold, Guid.NewGuid());

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);

        Assert.False(report.Applied);
        Assert.Contains(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);

        await using var check = Context(targetCs);
        Assert.Equal("Diverged", (await check.Walls.FirstAsync(w => w.Id == wallId)).Name);
        Assert.False(await check.Holds.AnyAsync(h => h.Id == addedHold));
    }

    [Fact]
    public async Task Replay_FailsFastOnSchemaParityMismatch()
    {
        var (wallId, addedHold, _, keepHold, package) = await CaptureAndExportAsync();
        package.MigrationId = "99999999999999_NotAMigration";

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await InitTargetAsync(targetCs, wallId, "Test Wall", keepHold, Guid.NewGuid());

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);

        Assert.False(report.Applied);
        Assert.NotEmpty(report.FatalErrors);
        Assert.Empty(report.Rows);

        await using var check = Context(targetCs);
        Assert.False(await check.Holds.AnyAsync(h => h.Id == addedHold));
    }

    [Fact]
    public async Task Replay_ReconstitutesABlobFromThePackage()
    {
        var wallId = Guid.NewGuid();
        var newPhoto = Enumerable.Range(0, 300).Select(i => (byte)(i % 256)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(newPhoto)).ToLowerInvariant();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);

        using (journal.BeginBatch("photo", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            (await db.Walls.FirstAsync(w => w.Id == wallId)).Photo = newPhoto;
            await db.SaveChangesAsync();
        }

        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "photo")]);
        Assert.Contains(package.Blobs, b => b.Sha256 == sha);

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await InitTargetAsync(targetCs, wallId, "Test Wall", Guid.NewGuid(), Guid.NewGuid());

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);
        Assert.True(report.Applied);

        await using var check = Context(targetCs);
        Assert.Equal(newPhoto, (await check.Walls.FirstAsync(w => w.Id == wallId)).Photo);
        Assert.Equal(1, await check.JournalBlobs.CountAsync(b => b.Sha256 == sha));
    }

    [Fact]
    public async Task Revert_RoundTripsShapePointsWithoutSpuriousConflict()
    {
        // FIX 1 regression: ShapePoints is a JSON-converted List<ShapePoint>. The recorded after-image
        // deserializes to a FRESH list, so a reference-equality comparison would report the identical
        // geometry as diverged and abort the revert. This test FAILS against reference equality.
        var before = new List<ShapePoint> { new() { Dx = 0.1, Dy = 0.1 }, new() { Dx = 0.2, Dy = 0.2 } };
        var after = new List<ShapePoint> { new() { Dx = 0.5, Dy = 0.5 } };
        var (wallId, holdId) = await CaptureShapeEditAsync(before, after);

        var result = await Reverter().RevertBatchAsync(await BatchIdAsync(sourceCs, "shape"));

        Assert.True(result.Reverted);
        Assert.Empty(result.Conflicts);

        await using var check = Context(sourceCs);
        var restored = (await check.Holds.FirstAsync(h => h.Id == holdId)).ShapePoints;
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal(0.1, restored[0].Dx);
        Assert.Equal(0.2, restored[1].Dy);
    }

    [Fact]
    public async Task Revert_ConflictsWhenShapePointsMutatedOutOfBand()
    {
        // FIX 1, other direction: genuinely different geometry must STILL be reported as a conflict.
        var before = new List<ShapePoint> { new() { Dx = 0.1, Dy = 0.1 } };
        var after = new List<ShapePoint> { new() { Dx = 0.5, Dy = 0.5 } };
        var (wallId, holdId) = await CaptureShapeEditAsync(before, after);

        await using (var db = Context(sourceCs))
        {
            (await db.Holds.FirstAsync(h => h.Id == holdId)).ShapePoints =
                new List<ShapePoint> { new() { Dx = 9.0, Dy = 9.0 } };
            await db.SaveChangesAsync();
        }

        var result = await Reverter().RevertBatchAsync(await BatchIdAsync(sourceCs, "shape"));

        Assert.False(result.Reverted);
        Assert.NotEmpty(result.Conflicts);

        await using var check = Context(sourceCs);
        var shape = (await check.Holds.FirstAsync(h => h.Id == holdId)).ShapePoints;
        Assert.Equal(9.0, shape![0].Dx);
    }

    [Fact]
    public async Task Replay_AppliesShapePointsUpdateWithoutSpuriousConflict()
    {
        // FIX 1 regression on the replay path: the before-image (a rebuilt List<ShapePoint>) must
        // compare equal to the target's identical geometry so the update applies rather than conflicts.
        var after = new List<ShapePoint> { new() { Dx = 0.5, Dy = 0.5 }, new() { Dx = 0.6, Dy = 0.6 } };
        var (wallId, holdId) = await CaptureShapeEditAsync(
            new List<ShapePoint> { new() { Dx = 0.1, Dy = 0.1 } }, after);
        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "shape")]);

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await SeedShapedHoldAsync(targetCs, wallId, holdId, new List<ShapePoint> { new() { Dx = 0.1, Dy = 0.1 } });

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);

        await using var check = Context(targetCs);
        var shape = (await check.Holds.FirstAsync(h => h.Id == holdId)).ShapePoints;
        Assert.NotNull(shape);
        Assert.Equal(2, shape!.Count);
        Assert.Equal(0.6, shape[1].Dy);
    }

    [Fact]
    public async Task Replay_ConflictsWhenTargetShapePointsDiffer()
    {
        // FIX 1, other direction on replay: different target geometry (matching neither before nor
        // after image) must be reported as a conflict, not silently treated as equal.
        var (wallId, holdId) = await CaptureShapeEditAsync(
            new List<ShapePoint> { new() { Dx = 0.1, Dy = 0.1 } },
            new List<ShapePoint> { new() { Dx = 0.5, Dy = 0.5 } });
        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "shape")]);

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await SeedShapedHoldAsync(targetCs, wallId, holdId, new List<ShapePoint> { new() { Dx = 9.0, Dy = 9.0 } });

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);

        Assert.False(report.Applied);
        Assert.Contains(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);

        await using var check = Context(targetCs);
        var shape = (await check.Holds.FirstAsync(h => h.Id == holdId)).ShapePoints;
        Assert.Equal(9.0, shape![0].Dx);
    }

    [Fact]
    public async Task Replay_ConflictsWhenDeletingBoulderWithProdOnlyDependent()
    {
        // FIX 2: deleting a Boulder cascades to its BoulderHolds. The target carries an extra
        // BoulderHold the batch never deleted; applying the cascade would silently drop it, so the
        // guard must raise a conflict and leave everything in place.
        var wallId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var boulderId = Guid.NewGuid();
        var holdA = Guid.NewGuid();
        var holdB = Guid.NewGuid();
        var holdC = Guid.NewGuid();

        await SeedBoulderSceneAsync(sourceCs, wallId, owner, boulderId, [holdA, holdB, holdC], [holdA, holdB]);

        using (journal.BeginBatch("delete-boulder", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            var holds = await db.BoulderHolds.Where(bh => bh.BoulderId == boulderId).ToListAsync();
            db.BoulderHolds.RemoveRange(holds);
            db.Boulders.Remove(await db.Boulders.FirstAsync(b => b.Id == boulderId));
            await db.SaveChangesAsync();
        }

        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "delete-boulder")]);

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        // Target has an EXTRA BoulderHold (holdC) the batch does not delete.
        await SeedBoulderSceneAsync(targetCs, wallId, owner, boulderId, [holdA, holdB, holdC], [holdA, holdB, holdC]);

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);

        Assert.False(report.Applied);
        Assert.Contains(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);

        await using var check = Context(targetCs);
        Assert.True(await check.Boulders.AnyAsync(b => b.Id == boulderId));
        Assert.Equal(3, await check.BoulderHolds.CountAsync(bh => bh.BoulderId == boulderId));
    }

    [Fact]
    public async Task Replay_DeletesBoulderWhenEveryDependentIsInTheBatch()
    {
        // FIX 2 clean case: all cascade dependents are themselves deleted by the batch, so the guard
        // permits the delete and it applies normally.
        var wallId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var boulderId = Guid.NewGuid();
        var holdA = Guid.NewGuid();
        var holdB = Guid.NewGuid();

        await SeedBoulderSceneAsync(sourceCs, wallId, owner, boulderId, [holdA, holdB], [holdA, holdB]);

        using (journal.BeginBatch("delete-boulder", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            var holds = await db.BoulderHolds.Where(bh => bh.BoulderId == boulderId).ToListAsync();
            db.BoulderHolds.RemoveRange(holds);
            db.Boulders.Remove(await db.Boulders.FirstAsync(b => b.Id == boulderId));
            await db.SaveChangesAsync();
        }

        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "delete-boulder")]);

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await SeedBoulderSceneAsync(targetCs, wallId, owner, boulderId, [holdA, holdB], [holdA, holdB]);

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);

        await using var check = Context(targetCs);
        Assert.False(await check.Boulders.AnyAsync(b => b.Id == boulderId));
        Assert.Equal(0, await check.BoulderHolds.CountAsync(bh => bh.BoulderId == boulderId));
    }

    [Fact]
    public async Task Replay_OfMultiWriteKey_IsIdempotentOnReRun()
    {
        // FIX 3: a batch that INSERTs a hold then UPDATEs it (a multi-write key, as the run→promote wall
        // update produces) must re-replay as an idempotent no-op. Pre-fix the first (insert) write was
        // divergence-checked against the FINAL target row, so the second replay reported a spurious
        // conflict ("row already present but differs from the recorded insert") — the 342-conflict bug.
        var wallId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);
        await CaptureInsertThenUpdateHoldAsync(wallId, holdId);

        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "multi")]);
        // Sanity: the one hold really is written twice in the batch (insert + update).
        Assert.Equal(2, package.Entries.Count(e => e.EntityType == nameof(Hold) && e.KeyJson.Contains(holdId.ToString())));

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await SeedWallOnlyTargetAsync(targetCs, wallId);

        // First replay onto the fresh target: the key applies.
        var first = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);
        Assert.True(first.Applied);
        Assert.DoesNotContain(first.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await using (var check = Context(targetCs))
        {
            Assert.True((await check.Holds.SingleAsync(h => h.Id == holdId)).NeedsReview);
        }

        // Re-replay the SAME package: every row is Skipped/AlreadyApplied, ZERO conflict (the regression).
        var second = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);
        Assert.NotEmpty(second.Rows);
        Assert.DoesNotContain(second.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        Assert.All(second.Rows, r => Assert.Equal(ChangeJournalReplayOutcome.SkippedAlreadyApplied, r.Outcome));
    }

    [Fact]
    public async Task Replay_OfMultiWriteKey_ConflictsWhenTargetMutatedOutOfBand()
    {
        // Genuine divergence is still caught after an apply: meddle with the row, then a re-replay of the
        // multi-write key reports a conflict instead of a spurious skip.
        var wallId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);
        await CaptureInsertThenUpdateHoldAsync(wallId, holdId);

        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "multi")]);

        using var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        var targetCs = target.ConnectionString;
        await SeedWallOnlyTargetAsync(targetCs, wallId);

        Assert.True((await Replayer(targetCs).ImportReplayAsync(package, dryRun: false)).Applied);

        await using (var db = Context(targetCs))
        {
            (await db.Holds.SingleAsync(h => h.Id == holdId)).X = 0.9;
            await db.SaveChangesAsync();
        }

        var report = await Replayer(targetCs).ImportReplayAsync(package, dryRun: false);
        Assert.False(report.Applied);
        Assert.Contains(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
    }

    [Fact]
    public async Task Revert_OfMultiWriteKey_ConflictsWhenInsertOnlyPropertyMutatedOutOfBand()
    {
        // FIX 4: the batch INSERTs a hold (setting X) then UPDATEs a DIFFERENT property (NeedsReview).
        // A human then edits X out-of-band. The old check verified only the net-final entry (the update,
        // whose after-image is just {NeedsReview}), so it MISSED the X edit and would silently delete the
        // row. The full net-final image check must now catch it and abort with a conflict.
        var wallId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);
        await CaptureInsertThenUpdateHoldAsync(wallId, holdId);

        await using (var db = Context(sourceCs))
        {
            (await db.Holds.SingleAsync(h => h.Id == holdId)).X = 0.9;
            await db.SaveChangesAsync();
        }

        var result = await Reverter().RevertBatchAsync(await BatchIdAsync(sourceCs, "multi"));

        Assert.False(result.Reverted);
        Assert.NotEmpty(result.Conflicts);

        // The divergent row survives — no silent delete.
        await using var check = Context(sourceCs);
        Assert.True(await check.Holds.AnyAsync(h => h.Id == holdId));
        Assert.Equal(0.9, (await check.Holds.SingleAsync(h => h.Id == holdId)).X);
    }

    [Fact]
    public async Task Revert_OfMultiWriteKey_CleanlyUndoesInsertAndUpdate()
    {
        // Clean case: with no out-of-band edit the multi-write key reverts exactly — the update is
        // unwound and the insert deleted, leaving no row behind.
        var wallId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);
        await CaptureInsertThenUpdateHoldAsync(wallId, holdId);

        await using (var pre = Context(sourceCs))
        {
            Assert.True((await pre.Holds.SingleAsync(h => h.Id == holdId)).NeedsReview);
        }

        var result = await Reverter().RevertBatchAsync(await BatchIdAsync(sourceCs, "multi"));

        Assert.True(result.Reverted);
        Assert.Empty(result.Conflicts);

        await using var check = Context(sourceCs);
        Assert.False(await check.Holds.AnyAsync(h => h.Id == holdId));
    }

    // Captures ONE batch that writes the same hold twice: an INSERT (setting X etc.) then an UPDATE that
    // touches only NeedsReview. Two SaveChanges under one BeginBatch scope, so both land in one batch with
    // continuous Seq — the run→promote multi-write shape, reduced to its essentials.
    private async Task CaptureInsertThenUpdateHoldAsync(Guid wallId, Guid holdId)
    {
        using (journal.BeginBatch("multi", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            var hold = new Hold
            {
                Id = holdId,
                WallId = wallId,
                X = 0.5,
                Y = 0.5,
                Radius = 0.02,
                Generation = 0,
                NeedsReview = false,
            };
            db.Holds.Add(hold);
            await db.SaveChangesAsync();

            hold.NeedsReview = true;
            await db.SaveChangesAsync();
        }
    }

    // A replay target holding only the wall (its scope must exist) but not the hold, so the multi-write
    // key applies from scratch.
    private async Task SeedWallOnlyTargetAsync(string cs, Guid wallId)
    {
        await using (var db = Context(cs))
        {
            await db.Database.EnsureCreatedAsync();
        }

        await SeedWallAsync(cs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3]);
    }

    private async Task<(Guid WallId, Guid HoldId)> CaptureShapeEditAsync(
        List<ShapePoint> before, List<ShapePoint> after)
    {
        var wallId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        await SeedShapedHoldAsync(sourceCs, wallId, holdId, before);

        using (journal.BeginBatch("shape", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            (await db.Holds.FirstAsync(h => h.Id == holdId)).ShapePoints = after;
            await db.SaveChangesAsync();
        }

        return (wallId, holdId);
    }

    private async Task SeedShapedHoldAsync(string cs, Guid wallId, Guid holdId, List<ShapePoint> shape)
    {
        await using var db = Context(cs);
        await db.Database.EnsureCreatedAsync();
        var owner = Guid.NewGuid();
        db.Users.Add(new User { Id = owner, Identifier = $"owner-{owner:N}@test", DisplayName = "Owner" });
        db.Walls.Add(new Wall
        {
            Id = wallId,
            Name = "Test Wall",
            OwnerId = owner,
            CurrentGeneration = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        });
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = owner, Role = WallRole.Admin });
        db.Holds.Add(new Hold
        {
            Id = holdId,
            WallId = wallId,
            X = 0.5,
            Y = 0.5,
            Radius = 0.02,
            Generation = 0,
            ShapePoints = shape,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedBoulderSceneAsync(
        string cs, Guid wallId, Guid ownerId, Guid boulderId, Guid[] holdIds, Guid[] boulderHoldIds)
    {
        await using var db = Context(cs);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = ownerId, Identifier = $"owner-{ownerId:N}@test", DisplayName = "Owner" });
        db.Walls.Add(new Wall
        {
            Id = wallId,
            Name = "Test Wall",
            OwnerId = ownerId,
            CurrentGeneration = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        });
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = ownerId, Role = WallRole.Admin });
        foreach (var holdId in holdIds)
        {
            db.Holds.Add(new Hold { Id = holdId, WallId = wallId, X = 0.5, Y = 0.5, Radius = 0.02, Generation = 0 });
        }

        // Fixed CreatedAt so the source and target boulders present an identical before-image.
        db.Boulders.Add(new Boulder
        {
            Id = boulderId,
            WallId = wallId,
            Name = "Boulder",
            CreatedByUserId = ownerId,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        foreach (var holdId in boulderHoldIds)
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulderId, HoldId = holdId });
        }

        await db.SaveChangesAsync();
    }

    private async Task<(Guid WallId, Guid AddedHold, Guid DoomedHold, Guid KeepHold, ChangeJournalPackage Package)> CaptureAndExportAsync()
    {
        var wallId = Guid.NewGuid();
        var keepHold = Guid.NewGuid();
        var doomedHold = Guid.NewGuid();
        await SeedWallAsync(sourceCs, wallId, Guid.NewGuid(), "Test Wall", [1, 2, 3], (keepHold, 0.1), (doomedHold, 0.2));

        Guid addedHold;
        using (journal.BeginBatch("edit", ChangeJournalScopeKind.Wall, wallId))
        {
            await using var db = Context(sourceCs);
            (await db.Walls.FirstAsync(w => w.Id == wallId)).Name = "Renamed";
            var added = new Hold { WallId = wallId, X = 0.9, Y = 0.9, Radius = 0.02, Generation = 0 };
            addedHold = added.Id;
            db.Holds.Add(added);
            db.Holds.Remove(await db.Holds.FirstAsync(h => h.Id == doomedHold));
            await db.SaveChangesAsync();
        }

        var package = await Exporter().ExportBatchesAsync([await BatchIdAsync(sourceCs, "edit")]);
        return (wallId, addedHold, doomedHold, keepHold, package);
    }

    private async Task InitTargetAsync(string cs, Guid wallId, string name, Guid keepHold, Guid doomedHold)
    {
        await using (var db = Context(cs))
        {
            await db.Database.EnsureCreatedAsync();
        }

        await SeedWallAsync(cs, wallId, Guid.NewGuid(), name, [1, 2, 3], (keepHold, 0.1), (doomedHold, 0.2));
    }

    private async Task SeedWallAsync(
        string cs, Guid wallId, Guid ownerId, string name, byte[] photo, params (Guid Id, double Xy)[] holds)
    {
        await using var db = Context(cs);
        db.Users.Add(new User { Id = ownerId, Identifier = $"owner-{ownerId:N}@test", DisplayName = "Owner" });
        db.Walls.Add(new Wall
        {
            Id = wallId,
            Name = name,
            OwnerId = ownerId,
            CurrentGeneration = 0,
            Photo = photo,
            PhotoContentType = "image/jpeg",
        });
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = ownerId, Role = WallRole.Admin });
        foreach (var (id, xy) in holds)
        {
            db.Holds.Add(new Hold { Id = id, WallId = wallId, X = xy, Y = xy, Radius = 0.02, Generation = 0 });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> BatchIdAsync(string cs, string label)
    {
        await using var db = Context(cs);
        return (await db.ChangeJournalBatches.FirstAsync(b => b.Label == label)).Id;
    }

    private ChangeJournalReverter Reverter() => new(new JournalingFactory(sourceCs, journal), journal);

    private ChangeJournalExporter Exporter() => new(new JournalingFactory(sourceCs, journal));

    private ChangeJournalReplayer Replayer(string cs) => new(new JournalingFactory(cs, journal), journal);

    private BlocwerkDbContext Context(string cs) => new JournalingFactory(cs, journal).CreateDbContext();

    /// <summary>A SQLite context factory that attaches the capture interceptor, so replay/revert writes journal.</summary>
    private sealed class JournalingFactory : IDbContextFactory<BlocwerkDbContext>
    {
        private readonly string connectionString;
        private readonly ChangeJournal journal;

        public JournalingFactory(string connectionString, ChangeJournal journal)
        {
            this.connectionString = connectionString;
            this.journal = journal;
        }

        public BlocwerkDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new ChangeJournalInterceptor(journal))
                .Options;
            var db = new SqliteBlocwerkDbContext(options);
            db.CurrentUserId = Guid.Empty;
            return db;
        }
    }
}
