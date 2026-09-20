using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Services;

/// <summary>
/// Reverts a whole <see cref="Entities.ChangeJournalBatch"/> by applying the INVERSE of each entry in
/// reverse <c>Seq</c> order, inside one transaction. A precondition guard first verifies the current
/// row state still matches what the batch RECORDED WE WROTE (the after-image for Insert/Update; the
/// row's absence for Delete); on any mismatch the whole revert is rolled back and the conflicts are
/// reported — it never clobbers divergent state. The revert's own inverse writes flow back through
/// the capture interceptor as a NEW batch, so a revert is itself journalled and replayable.
/// <para>
/// KNOWN GAP — reverting a wall-update batch leaves its session header behind. The batch journals the
/// <see cref="Hold"/> and <see cref="WallPanel"/> rows the update wrote, so reverting deletes them, and
/// the <see cref="WallUpdateHoldDecision"/>/<see cref="WallUpdateNeighbourDecision"/> rows cascade away
/// with their holds. The <see cref="WallUpdateSession"/> row itself is NOT journalled (it is working
/// state, not wall content), so it survives — still <see cref="WallUpdateSessionStatus.Open"/>, now with
/// no staged panels and no decisions under it. The next <c>StageAsync</c> on that wall then refuses with
/// a <see cref="WallUpdateSessionConflictException"/> naming a session that has nothing left in it, and
/// the way out is the wizard's destructive-looking "Discard theirs &amp; start over" — which in this
/// state actually destroys nothing. Deliberately not fixed here: teaching the reverter about a
/// non-journalled header would make it aggregate-aware, which is exactly what it is not.
/// </para>
/// </summary>
public sealed class ChangeJournalReverter
{
    private readonly IDbContextFactory<BlocwerkDbContext> factory;
    private readonly ChangeJournal journal;

    public ChangeJournalReverter(IDbContextFactory<BlocwerkDbContext> factory, ChangeJournal journal)
    {
        this.factory = factory;
        this.journal = journal;
    }

    public async Task<ChangeJournalRevertResult> RevertBatchAsync(
        Guid batchId, bool force = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CurrentUserId = Guid.Empty;

        var batch = await db.ChangeJournalBatches.FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return new ChangeJournalRevertResult(false, batchId, null, [], "Batch not found.");
        }

        // Reverse Seq is the inverse of the recorded save order, which is FK-safe; EF still reorders
        // the physical writes within the single SaveChanges to respect foreign keys.
        var entries = await db.ChangeJournalEntries
            .Where(e => e.BatchId == batchId)
            .OrderByDescending(e => e.Seq)
            .ToListAsync(cancellationToken);

        var resolveBlob = await LoadBlobResolverAsync(db, entries, cancellationToken);

        // Reverting an Insert deletes the row, so those keys are the ones this revert removes; the
        // cascade guard uses them to tell a batch-known dependent from a prod-only one.
        var deletedByBatch = entries
            .Where(e => e.Op == ChangeJournalOp.Insert)
            .Select(e => (e.EntityType, e.KeyJson))
            .ToHashSet();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var conflicts = new List<ChangeJournalConflict>();

        // Divergence is checked per KEY against the batch's full NET-FINAL image — every property the
        // batch set across ALL its entries, not just the last update's touched props. A key written more
        // than once in this batch (the run→promote wall update INSERTs a staged panel then UPDATEs it)
        // therefore catches an out-of-band edit to a property an earlier entry set but a later one never
        // touched, which a last-entry-only check would silently miss and then delete.
        foreach (var group in entries.GroupBy(e => (e.EntityType, e.KeyJson)))
        {
            var ordered = group.OrderBy(e => e.Seq).ToList();
            var conflict = await CheckKeyDivergenceAsync(db, ordered, resolveBlob, cancellationToken);
            if (conflict is not null)
            {
                conflicts.Add(conflict);
            }
        }

        // Reverting an Insert deletes the row, so each Insert (even a superseded one) is guarded against
        // silently cascading over prod-only dependents. An Update's inverse only restores scalars.
        foreach (var entry in entries.Where(e => e.Op == ChangeJournalOp.Insert))
        {
            var conflict = await CheckInsertCascadeAsync(db, entry, deletedByBatch, cancellationToken);
            if (conflict is not null)
            {
                conflicts.Add(conflict);
            }
        }

        if (conflicts.Count > 0 && !force)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ChangeJournalRevertResult(false, batchId, null, conflicts, null);
        }

        Guid? revertBatchId;
        using (journal.BeginBatch($"revert:{batch.Label}", batch.ScopeKind, batch.ScopeId))
        {
            foreach (var entry in entries)
            {
                await ApplyInverseAsync(db, entry, resolveBlob, cancellationToken);
            }

            // Not allow-listed, so this status flip is not itself journalled.
            batch.Status = ChangeJournalStatus.Reverted;
            await db.SaveChangesAsync(cancellationToken);
            revertBatchId = journal.Current?.BatchId;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChangeJournalRevertResult(true, batchId, revertBatchId, conflicts, null);
    }

    /// <summary>
    /// Verifies the current row still matches the batch's NET effect on this key (its full net-final
    /// image, or absence when the net effect is a delete). Attributes any conflict to the last entry.
    /// </summary>
    private static async Task<ChangeJournalConflict?> CheckKeyDivergenceAsync(
        BlocwerkDbContext db, IReadOnlyList<ChangeJournalEntry> ordered, Func<string, byte[]?> resolveBlob, CancellationToken ct)
    {
        var netFinal = ordered[^1];
        var (clrType, entityType) = ResolveOrNull(db, netFinal);
        if (clrType is null || entityType is null)
        {
            return Conflict(netFinal, $"Unknown entity type '{netFinal.EntityType}'.");
        }

        var effect = ChangeJournalKeyNetEffect.Reduce(
            entityType, ordered.Select(e => (e.Op, e.BeforeJson, e.AfterJson)).ToList(), resolveBlob);
        var found = await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, netFinal.KeyJson, ct);

        if (effect.NetIsAbsent)
        {
            return found is null
                ? null
                : Conflict(netFinal, "Row expected absent (the batch's net effect is a delete) but is present.");
        }

        if (found is null)
        {
            return Conflict(netFinal, "Row expected present (journal recorded a write) but is absent.");
        }

        return ChangeJournalEntityAccessor.CurrentMatches(db.Entry(found), effect.NetFinalImage!, out var detail)
            ? null
            : Conflict(netFinal, $"Current state no longer matches the batch's net effect: {detail}.");
    }

    private static async Task<ChangeJournalConflict?> CheckInsertCascadeAsync(
        BlocwerkDbContext db, ChangeJournalEntry entry,
        IReadOnlySet<(string EntityType, string KeyJson)> deletedByBatch, CancellationToken ct)
    {
        var (clrType, entityType) = ResolveOrNull(db, entry);
        if (clrType is null || entityType is null)
        {
            // The divergence pass already reports the unknown type; nothing to guard here.
            return null;
        }

        var found = await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, entry.KeyJson, ct);
        if (found is null)
        {
            return null;
        }

        var blocking = await ChangeJournalCascadeGuard.FindBlockingDependentsAsync(db, db.Entry(found), deletedByBatch, ct);
        return blocking.Count > 0
            ? Conflict(entry,
                $"Reverting this insert would cascade to {blocking.Count} dependent row(s) not in this batch: {string.Join(", ", blocking.Take(5))}.")
            : null;
    }

    private static async Task ApplyInverseAsync(
        BlocwerkDbContext db, ChangeJournalEntry entry, Func<string, byte[]?> resolveBlob, CancellationToken ct)
    {
        var (clrType, entityType) = ResolveOrNull(db, entry);
        if (clrType is null || entityType is null)
        {
            return;
        }

        switch (entry.Op)
        {
            case ChangeJournalOp.Insert:
                // We inserted the row; the inverse is to delete it.
                var inserted = await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, entry.KeyJson, ct);
                if (inserted is not null)
                {
                    db.Remove(inserted);
                }

                break;

            case ChangeJournalOp.Delete:
                // We deleted the row; the inverse is to re-insert its full before-image.
                var beforeAll = ChangeJournalValueWriter.DeserializeValues(entityType, entry.BeforeJson ?? "{}", resolveBlob);
                ChangeJournalEntityAccessor.InsertFrom(db, clrType, beforeAll);
                break;

            case ChangeJournalOp.Update:
                // We changed some properties; the inverse is to restore their before-values.
                var target = await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, entry.KeyJson, ct);
                if (target is not null)
                {
                    var before = ChangeJournalValueWriter.DeserializeValues(entityType, entry.BeforeJson ?? "{}", resolveBlob);
                    ChangeJournalEntityAccessor.ApplyValues(db.Entry(target), before);
                }

                break;
        }
    }

    private static (Type? Clr, IEntityType? Meta) ResolveOrNull(BlocwerkDbContext db, ChangeJournalEntry entry)
    {
        var clrType = ChangeJournalEntityAccessor.ResolveType(db, entry.EntityType);
        return clrType is null ? (null, null) : (clrType, db.Model.FindEntityType(clrType));
    }

    private static ChangeJournalConflict Conflict(ChangeJournalEntry entry, string reason) =>
        new(entry.Seq, entry.EntityType, entry.KeyJson, reason);

    private static async Task<Func<string, byte[]?>> LoadBlobResolverAsync(
        BlocwerkDbContext db, IEnumerable<ChangeJournalEntry> entries, CancellationToken ct)
    {
        var shas = entries
            .SelectMany(e => ChangeJournalValueWriter.BlobShas(e.BeforeJson)
                .Concat(ChangeJournalValueWriter.BlobShas(e.AfterJson)))
            .Distinct()
            .ToList();

        var blobs = shas.Count == 0
            ? []
            : await db.JournalBlobs.Where(b => shas.Contains(b.Sha256)).ToDictionaryAsync(b => b.Sha256, b => b.Bytes, ct);

        return sha => blobs.TryGetValue(sha, out var bytes) ? bytes : null;
    }
}
