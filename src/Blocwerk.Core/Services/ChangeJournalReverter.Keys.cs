// <copyright file="ChangeJournalReverter.Keys.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>Reverting ONE row's part of several batches (the "undo this one hold" of a review), leaving the rest of them as they are.</summary>
public sealed partial class ChangeJournalReverter
{
    /// <summary>
    /// Reverts what the given batches recorded for one row (<paramref name="entityType"/> + <paramref name="keyJson"/>),
    /// newest batch first, in one transaction and one new journal batch. Each batch's part goes through the same
    /// divergence guard as a whole-batch revert, checked against the row as the newer batches' inverses already left it
    /// — so a row someone edited since is refused (and nothing is written), never clobbered. The batches themselves
    /// stay <see cref="ChangeJournalStatus.Recorded"/>: only one of their rows was taken back.
    /// </summary>
    /// <param name="batchIdsNewestFirst">The batches whose entries for the row are reverted, the newest first.</param>
    /// <param name="entityType">The entity type name as journalled (e.g. "Hold").</param>
    /// <param name="keyJson">The row's key as journalled.</param>
    /// <param name="label">The label of the revert's own batch.</param>
    /// <param name="scopeKind">Its scope kind.</param>
    /// <param name="scopeId">Its scope id.</param>
    /// <param name="actingUserId">Who reverts (the revert batch's actor).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was reverted, the revert batch, and the conflicts that refused it.</returns>
    public async Task<ChangeJournalRevertResult> RevertKeyAsync(
        IReadOnlyList<Guid> batchIdsNewestFirst,
        string entityType,
        string keyJson,
        string label,
        ChangeJournalScopeKind scopeKind,
        Guid? scopeId,
        Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batchIdsNewestFirst);
        var first = batchIdsNewestFirst.Count > 0 ? batchIdsNewestFirst[0] : Guid.Empty;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CurrentUserId = actingUserId;
        var ids = batchIdsNewestFirst.ToList();
        var entries = await db.ChangeJournalEntries
            .Where(e => ids.Contains(e.BatchId) && e.EntityType == entityType && e.KeyJson == keyJson)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return new ChangeJournalRevertResult(false, first, null, [], "Nothing was recorded for this row.");
        }

        var resolveBlob = await LoadBlobResolverAsync(db, entries, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var batchId in ids)
        {
            var ordered = entries.Where(e => e.BatchId == batchId).OrderBy(e => e.Seq).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }

            if (await CheckKeyDivergenceAsync(db, ordered, resolveBlob, cancellationToken) is { } conflict)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ChangeJournalRevertResult(false, batchId, null, [conflict], null);
            }

            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                await ApplyInverseAsync(db, ordered[i], resolveBlob, cancellationToken);
            }
        }

        Guid? revertBatchId;
        using (journal.BeginBatch(label, scopeKind, scopeId))
        {
            await db.SaveChangesAsync(cancellationToken);
            revertBatchId = journal.Current?.BatchId;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChangeJournalRevertResult(true, first, revertBatchId, [], null);
    }
}
