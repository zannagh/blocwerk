using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Services;

/// <summary>
/// Applies a <see cref="ChangeJournalPackage"/> exported elsewhere onto THIS database ("prod"),
/// re-applying the recorded field values to the same GUID keys — never re-running the process that
/// produced them. Fail-fast gates first: the package's EF migration id must equal this database's
/// (schema parity), and every scoped aggregate it touches must still exist here. Then a per-row pass
/// classifies each entry as applied / skipped-already-applied / conflict against its before-image.
/// A dry run reports without writing; a real run commits in one transaction and journals the replayed
/// writes as a NEW batch, aborting on any conflict unless forced.
/// </summary>
public sealed class ChangeJournalReplayer
{
    private readonly IDbContextFactory<BlocwerkDbContext> factory;
    private readonly ChangeJournal journal;

    public ChangeJournalReplayer(IDbContextFactory<BlocwerkDbContext> factory, ChangeJournal journal)
    {
        this.factory = factory;
        this.journal = journal;
    }

    public async Task<ChangeJournalReplayReport> ImportReplayAsync(
        ChangeJournalPackage package, bool dryRun, bool force = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CurrentUserId = Guid.Empty;

        var fatal = await GateAsync(db, package, cancellationToken);
        if (fatal.Count > 0 && !force)
        {
            return new ChangeJournalReplayReport(false, dryRun, [], fatal, null);
        }

        var resolveBlob = BlobResolver(package);

        // The set of rows THIS batch itself deletes, so the cascade guard can tell a dependent the
        // batch already accounts for from a prod-only dependent that a delete would silently remove.
        var deletedByBatch = package.Entries
            .Where(e => e.Op == ChangeJournalOp.Delete)
            .Select(e => (e.EntityType, e.KeyJson))
            .ToHashSet();

        // Phase 1 — decide the outcome for each KEY (a key written more than once in this package — the
        // run→promote wall update INSERTs a staged panel then UPDATEs it live — is evaluated by its NET
        // effect, not by any single intermediate write). Every entry of a key inherits that one outcome,
        // so re-replaying an already-applied package is an idempotent no-op, not a spurious conflict.
        var outcomeByKey = new Dictionary<(string EntityType, string KeyJson), (ChangeJournalReplayOutcome Outcome, string? Detail)>();
        foreach (var group in package.Entries.GroupBy(e => (e.EntityType, e.KeyJson)))
        {
            var ordered = group.OrderBy(e => e.Seq).ToList();
            outcomeByKey[group.Key] = await EvaluateKeyAsync(db, ordered, resolveBlob, deletedByBatch, cancellationToken);
        }

        var rows = package.Entries
            .Select(e =>
            {
                var (outcome, detail) = outcomeByKey[(e.EntityType, e.KeyJson)];
                return Row(e, outcome, detail);
            })
            .ToList();

        var hasConflict = rows.Any(r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        if (dryRun)
        {
            return new ChangeJournalReplayReport(false, true, rows, fatal, null);
        }

        if (hasConflict && !force)
        {
            return new ChangeJournalReplayReport(false, false, rows, fatal, null);
        }

        // Phase 2 — apply the rows that need applying, in one transaction, journalled as a new batch.
        var scope = package.Batches.Select(b => (b.ScopeKind, b.ScopeId)).FirstOrDefault();
        Guid? replayBatchId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        using (journal.BeginBatch("replay", scope.ScopeKind, scope.ScopeId))
        {
            for (var i = 0; i < package.Entries.Count; i++)
            {
                if (rows[i].Outcome == ChangeJournalReplayOutcome.Applied)
                {
                    await ApplyAsync(db, package.Entries[i], resolveBlob, cancellationToken);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            replayBatchId = journal.Current?.BatchId;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChangeJournalReplayReport(true, false, rows, fatal, replayBatchId);
    }

    private static async Task<List<string>> GateAsync(
        BlocwerkDbContext db, ChangeJournalPackage package, CancellationToken ct)
    {
        var fatal = new List<string>();
        var targetMigration = await ChangeJournalMarkers.MigrationIdAsync(db, ct);
        if (package.MigrationId != targetMigration)
        {
            fatal.Add($"Schema parity mismatch: package migration '{package.MigrationId}' != target '{targetMigration}'.");
        }

        foreach (var marker in package.BaseMarkers)
        {
            var current = await ChangeJournalMarkers.ComputeAsync(db, marker.ScopeKind, marker.ScopeId, ct);
            if (marker.ScopeKind == ChangeJournalScopeKind.Wall && current.Generation < 0)
            {
                fatal.Add($"Scope diverged: wall {marker.ScopeId} does not exist on the target.");
            }
        }

        return fatal;
    }

    /// <summary>
    /// Decides one key's replay outcome from its ordered entries, judged against the batch's NET effect:
    /// already at the net effect → skip (idempotent); else at the recorded starting precondition (absent
    /// for an insert-first key, the first before-image otherwise) → apply the key's entries in Seq order;
    /// else → conflict. A net-delete that would apply still runs the cascade guard.
    /// </summary>
    private static async Task<(ChangeJournalReplayOutcome Outcome, string? Detail)> EvaluateKeyAsync(
        BlocwerkDbContext db, IReadOnlyList<ChangeJournalPackageEntry> ordered,
        Func<string, byte[]?> resolveBlob, IReadOnlySet<(string EntityType, string KeyJson)> deletedByBatch,
        CancellationToken ct)
    {
        var first = ordered[0];
        var (clrType, entityType) = ResolveOrNull(db, first.EntityType);
        if (clrType is null || entityType is null)
        {
            return (ChangeJournalReplayOutcome.Conflict, $"Unknown entity type '{first.EntityType}'.");
        }

        var effect = ChangeJournalKeyNetEffect.Reduce(
            entityType, ordered.Select(e => (e.Op, e.BeforeJson, e.AfterJson)).ToList(), resolveBlob);
        var found = await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, first.KeyJson, ct);

        // Already at the batch's net effect? Idempotent no-op — the fix for re-replay reporting conflicts.
        if (effect.NetIsAbsent)
        {
            if (found is null)
            {
                return (ChangeJournalReplayOutcome.SkippedAlreadyApplied, "Net effect already applied: row absent.");
            }
        }
        else if (found is not null && ChangeJournalEntityAccessor.CurrentMatches(db.Entry(found), effect.NetFinalImage!, out _))
        {
            return (ChangeJournalReplayOutcome.SkippedAlreadyApplied, "Row already holds the batch's net effect.");
        }

        // A fresh apply requires the recorded starting precondition for this key.
        var preconditionMet = effect.FirstRequiresAbsent
            ? found is null
            : found is not null && ChangeJournalEntityAccessor.CurrentMatches(db.Entry(found), effect.FirstBeforeImage!, out _);
        if (!preconditionMet)
        {
            return (ChangeJournalReplayOutcome.Conflict, PreconditionConflictDetail(effect, found));
        }

        // Applying leaves the row absent (net delete) — refuse to silently cascade over prod-only dependents.
        if (effect.NetIsAbsent && found is not null)
        {
            var blocking = await ChangeJournalCascadeGuard.FindBlockingDependentsAsync(db, db.Entry(found), deletedByBatch, ct);
            if (blocking.Count > 0)
            {
                return (ChangeJournalReplayOutcome.Conflict,
                    $"Delete would cascade to {blocking.Count} dependent row(s) not in this batch: {string.Join(", ", blocking.Take(5))}.");
            }
        }

        return (ChangeJournalReplayOutcome.Applied, null);
    }

    private static string PreconditionConflictDetail(ChangeJournalKeyNetEffect effect, object? found)
    {
        if (effect.FirstRequiresAbsent)
        {
            return "Row already present but differs from the recorded insert.";
        }

        return found is null
            ? "Row absent; expected the recorded before-image."
            : "Row diverged from the recorded before-image.";
    }

    private static async Task ApplyAsync(
        BlocwerkDbContext db, ChangeJournalPackageEntry entry, Func<string, byte[]?> resolveBlob, CancellationToken ct)
    {
        var (clrType, entityType) = ResolveOrNull(db, entry.EntityType);
        if (clrType is null || entityType is null)
        {
            return;
        }

        switch (entry.Op)
        {
            case ChangeJournalOp.Insert:
                var values = ChangeJournalValueWriter.DeserializeValues(entityType, entry.AfterJson ?? "{}", resolveBlob);
                ChangeJournalEntityAccessor.InsertFrom(db, clrType, values);
                break;

            case ChangeJournalOp.Update:
                var target = await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, entry.KeyJson, ct);
                if (target is not null)
                {
                    var after = ChangeJournalValueWriter.DeserializeValues(entityType, entry.AfterJson ?? "{}", resolveBlob);
                    ChangeJournalEntityAccessor.ApplyValues(db.Entry(target), after);
                }

                break;

            case ChangeJournalOp.Delete:
                var doomed = await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, entry.KeyJson, ct);
                if (doomed is not null)
                {
                    db.Remove(doomed);
                }

                break;
        }
    }

    private static Func<string, byte[]?> BlobResolver(ChangeJournalPackage package)
    {
        var byShaBytes = package.Blobs
            .GroupBy(b => b.Sha256)
            .ToDictionary(g => g.Key, g => Convert.FromBase64String(g.First().Base64));
        return sha => byShaBytes.TryGetValue(sha, out var bytes) ? bytes : null;
    }

    private static (Type? Clr, IEntityType? Meta) ResolveOrNull(BlocwerkDbContext db, string entityTypeName)
    {
        var clrType = ChangeJournalEntityAccessor.ResolveType(db, entityTypeName);
        return clrType is null ? (null, null) : (clrType, db.Model.FindEntityType(clrType));
    }

    private static ChangeJournalReplayRow Row(
        ChangeJournalPackageEntry entry, ChangeJournalReplayOutcome outcome, string? detail) =>
        new(entry.Seq, entry.EntityType, entry.KeyJson, outcome, detail);
}
