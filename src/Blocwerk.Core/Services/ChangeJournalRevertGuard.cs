using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Services;

/// <summary>
/// The precondition guards a revert runs before it inverts anything, lifted out of
/// <see cref="ChangeJournalReverter"/> verbatim so the dry-run preview and the real revert execute the
/// SAME code. Read-only throughout: it loads entries, resolves blobs and inspects current rows, but it
/// never mutates the context and never calls SaveChanges.
/// </summary>
internal static class ChangeJournalRevertGuard
{
    /// <summary>
    /// Loads the batch's entries and runs both guards against the live rows. Purely a read — the caller
    /// decides whether to act on <see cref="ChangeJournalRevertPlan.Conflicts"/>.
    /// </summary>
    public static async Task<ChangeJournalRevertPlan> BuildAsync(
        BlocwerkDbContext db, Guid batchId, CancellationToken ct)
    {
        // Reverse Seq is the inverse of the recorded save order, which is FK-safe; EF still reorders
        // the physical writes within the single SaveChanges to respect foreign keys.
        var entries = await db.ChangeJournalEntries
            .Where(e => e.BatchId == batchId)
            .OrderByDescending(e => e.Seq)
            .ToListAsync(ct);

        var resolveBlob = await LoadBlobResolverAsync(db, entries, ct);
        var conflicts = await CollectConflictsAsync(db, entries, resolveBlob, ct);
        return new ChangeJournalRevertPlan(entries, resolveBlob, conflicts);
    }

    /// <summary>Runs the divergence pass over every key, then the cascade pass over every Insert.</summary>
    private static async Task<IReadOnlyList<ChangeJournalConflict>> CollectConflictsAsync(
        BlocwerkDbContext db, IReadOnlyList<ChangeJournalEntry> entries, Func<string, byte[]?> resolveBlob, CancellationToken ct)
    {
        // Reverting an Insert deletes the row, so those keys are the ones this revert removes; the
        // cascade guard uses them to tell a batch-known dependent from a prod-only one.
        var deletedByBatch = entries
            .Where(e => e.Op == ChangeJournalOp.Insert)
            .Select(e => (e.EntityType, e.KeyJson))
            .ToHashSet();

        var conflicts = new List<ChangeJournalConflict>();

        // Divergence is checked per KEY against the batch's full NET-FINAL image — every property the
        // batch set across ALL its entries, not just the last update's touched props. A key written more
        // than once in this batch (the run→promote wall update INSERTs a staged panel then UPDATEs it)
        // therefore catches an out-of-band edit to a property an earlier entry set but a later one never
        // touched, which a last-entry-only check would silently miss and then delete.
        foreach (var group in entries.GroupBy(e => (e.EntityType, e.KeyJson)))
        {
            var ordered = group.OrderBy(e => e.Seq).ToList();
            var conflict = await CheckKeyDivergenceAsync(db, ordered, resolveBlob, ct);
            if (conflict is not null)
            {
                conflicts.Add(conflict);
            }
        }

        // Reverting an Insert deletes the row, so each Insert (even a superseded one) is guarded against
        // silently cascading over prod-only dependents. An Update's inverse only restores scalars.
        foreach (var entry in entries.Where(e => e.Op == ChangeJournalOp.Insert))
        {
            var conflict = await CheckInsertCascadeAsync(db, entry, deletedByBatch, ct);
            if (conflict is not null)
            {
                conflicts.Add(conflict);
            }
        }

        return conflicts;
    }

    public static (Type? Clr, IEntityType? Meta) ResolveOrNull(BlocwerkDbContext db, ChangeJournalEntry entry)
    {
        var clrType = ChangeJournalEntityAccessor.ResolveType(db, entry.EntityType);
        return clrType is null ? (null, null) : (clrType, db.Model.FindEntityType(clrType));
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
