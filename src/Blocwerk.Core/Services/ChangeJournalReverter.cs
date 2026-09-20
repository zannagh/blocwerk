using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Reverts a whole <see cref="Entities.ChangeJournalBatch"/> by applying the INVERSE of each entry in
/// reverse <c>Seq</c> order, inside one transaction. A precondition guard first verifies the current
/// row state still matches what the batch RECORDED WE WROTE (see <see cref="ChangeJournalRevertGuard"/>,
/// which <see cref="PreviewRevertAsync"/> and <see cref="RevertBatchAsync"/> share); on any mismatch the
/// whole revert is rolled back and the conflicts are reported — it never clobbers divergent state. The
/// revert's own inverse writes flow back through the capture interceptor as a NEW batch, so a revert is
/// itself journalled and replayable.
/// <para>
/// A revert CLAIMS its batch first: a conditional <c>UPDATE … WHERE Status = Recorded</c> inside the
/// transaction. That is the whole concurrency story — an already-reverted batch and a second operator
/// racing the first both lose the claim and come back as
/// <see cref="ChangeJournalRevertOutcome.AlreadyReverted"/> with nothing written, instead of applying a
/// second inverse over the first one's result.
/// </para>
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
/// non-journalled header would make it aggregate-aware, which is exactly what it is not. It IS surfaced:
/// see <see cref="ChangeJournalRevertPreview.RequiresWallUpdateSessionCleanup"/>.
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

    /// <summary>
    /// Runs BOTH precondition guards against the live rows and reports what a revert would hit —
    /// without opening a transaction and without writing anything. Uses the same
    /// <see cref="ChangeJournalRevertGuard"/> the real revert does, so the two cannot diverge.
    /// </summary>
    public async Task<ChangeJournalRevertPreview> PreviewRevertAsync(
        Guid batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ChangeJournalBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return new ChangeJournalRevertPreview(
                batchId, false, string.Empty, ChangeJournalStatus.Recorded,
                ChangeJournalScopeKind.None, null, 0, [], false, false, null);
        }

        // A batch that is no longer Recorded cannot be reverted at all, and running the divergence guard
        // over an already-reverted batch would report a conflict for every single key — a wall of noise
        // that says nothing beyond "already reverted". Answer that directly instead.
        var entryCount = await db.ChangeJournalEntries.CountAsync(e => e.BatchId == batchId, cancellationToken);
        var orphanSession = await OpenWallUpdateSessionAsync(db, batch, cancellationToken);
        if (batch.Status != ChangeJournalStatus.Recorded)
        {
            return new ChangeJournalRevertPreview(
                batchId, true, batch.Label, batch.Status, batch.ScopeKind, batch.ScopeId,
                entryCount, [], false, orphanSession is not null, orphanSession);
        }

        var plan = await ChangeJournalRevertGuard.BuildAsync(db, batchId, cancellationToken);
        var conflicts = await ChangeJournalKeyDescriber.DescribeAsync(db, plan.Conflicts, cancellationToken);

        return new ChangeJournalRevertPreview(
            batchId,
            true,
            batch.Label,
            batch.Status,
            batch.ScopeKind,
            batch.ScopeId,
            plan.Entries.Count,
            conflicts,
            conflicts.Count == 0,
            orphanSession is not null,
            orphanSession);
    }

    /// <param name="actingUserId">
    /// The operator performing the revert. Stamped onto the context so the capture interceptor records
    /// it as the revert batch's <c>Actor</c> — a destructive operator action must say who did it. Null
    /// (the default, used by the dev harness) leaves the revert batch unattributed, as before.
    /// </param>
    public async Task<ChangeJournalRevertResult> RevertBatchAsync(
        Guid batchId,
        Guid? actingUserId = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CurrentUserId = actingUserId ?? Guid.Empty;

        var batch = await db.ChangeJournalBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return new ChangeJournalRevertResult(
                false, batchId, null, [], "Batch not found.", ChangeJournalRevertOutcome.BatchNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ClaimAsync(db, batchId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ChangeJournalRevertResult(
                false, batchId, null, [], "Batch is already reverted or is being reverted right now.",
                ChangeJournalRevertOutcome.AlreadyReverted);
        }

        var plan = await ChangeJournalRevertGuard.BuildAsync(db, batchId, cancellationToken);
        if (plan.Conflicts.Count > 0 && !force)
        {
            // Rolls the claim back with everything else, so the batch stays Recorded and revertable.
            await transaction.RollbackAsync(cancellationToken);
            return new ChangeJournalRevertResult(
                false, batchId, null, plan.Conflicts, null, ChangeJournalRevertOutcome.Blocked);
        }

        Guid? revertBatchId;
        using (journal.BeginBatch($"revert:{batch.Label}", batch.ScopeKind, batch.ScopeId))
        {
            foreach (var entry in plan.Entries)
            {
                await ApplyInverseAsync(db, entry, plan.ResolveBlob, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            revertBatchId = journal.Current?.BatchId;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChangeJournalRevertResult(
            true, batchId, revertBatchId, plan.Conflicts, null, ChangeJournalRevertOutcome.Reverted);
    }

    /// <summary>
    /// Claims the batch for this revert: a conditional update that flips Recorded → Reverted and returns
    /// whether it won. Running inside the caller's transaction is what makes it a lock as well as a test —
    /// a concurrent revert of the same batch blocks on the row until this transaction ends, then finds
    /// zero rows to claim. Not allow-listed, and an ExecuteUpdate besides, so the flip is not journalled.
    /// </summary>
    private static async Task<bool> ClaimAsync(BlocwerkDbContext db, Guid batchId, CancellationToken ct)
    {
        var claimed = await db.ChangeJournalBatches
            .Where(b => b.Id == batchId && b.Status == ChangeJournalStatus.Recorded)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, ChangeJournalStatus.Reverted), ct);
        return claimed > 0;
    }

    /// <summary>
    /// The Open <see cref="WallUpdateSession"/> a revert of this batch would orphan, or null. See the
    /// KNOWN GAP note on the class: the header is not journalled, so the revert cannot clean it up.
    /// </summary>
    private static async Task<Guid?> OpenWallUpdateSessionAsync(
        BlocwerkDbContext db, ChangeJournalBatch batch, CancellationToken ct)
    {
        if (batch.Label != ChangeJournal.WallUpdateBatchLabel
            || batch.ScopeKind != ChangeJournalScopeKind.Wall
            || batch.ScopeId is null)
        {
            return null;
        }

        return await db.WallUpdateSessions
            .AsNoTracking()
            .Where(s => s.WallId == batch.ScopeId && s.Status == WallUpdateSessionStatus.Open)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task ApplyInverseAsync(
        BlocwerkDbContext db, ChangeJournalEntry entry, Func<string, byte[]?> resolveBlob, CancellationToken ct)
    {
        var (clrType, entityType) = ChangeJournalRevertGuard.ResolveOrNull(db, entry);
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
}
