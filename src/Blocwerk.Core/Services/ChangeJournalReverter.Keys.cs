// <copyright file="ChangeJournalReverter.Keys.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>Reverting ONE row's part of several batches (the "undo this one hold" of a review), leaving the rest of them as they are.</summary>
public sealed partial class ChangeJournalReverter
{
    /// <summary>
    /// Reverts what the given batches recorded for one row (the request's entity type and key),
    /// newest batch first, in one transaction and one new journal batch. Each batch's part goes through a divergence guard,
    /// checked against the row as the newer batches' inverses already left it, so a row someone edited since is refused
    /// (and nothing is written), never clobbered. The batches themselves stay <see cref="ChangeJournalStatus.Recorded"/>.
    /// </summary>
    /// <remarks>
    /// With owned properties in the request, only those columns must still be as the batch left them (they are what the
    /// revert is about, e.g. a hold's outline); every other column the batch set is put back only where it still has the
    /// batch's value, and left alone where later work (a 3D refinement re-deriving sizes) changed it since. Without it,
    /// every column the batch set must match, as for a whole-batch revert.
    /// </remarks>
    /// <param name="request">Which row, from which batches, and how the revert batch is labelled.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was reverted, the revert batch, and the conflicts that refused it.</returns>
    public async Task<ChangeJournalRevertResult> RevertKeyAsync(ChangeJournalKeyRevert request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.BatchIdsNewestFirst.ToList();
        var first = ids.Count > 0 ? ids[0] : Guid.Empty;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CurrentUserId = request.ActingUserId;
        var entries = await db.ChangeJournalEntries
            .Where(e => ids.Contains(e.BatchId) && e.EntityType == request.EntityType && e.KeyJson == request.KeyJson)
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

            var conflict = request.OwnedProperties is null
                ? await RevertWholeKeyAsync(db, ordered, resolveBlob, cancellationToken)
                : await RevertOwnedAsync(db, ordered, request.OwnedProperties, resolveBlob, cancellationToken);
            if (conflict is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ChangeJournalRevertResult(false, batchId, null, [conflict], null);
            }
        }

        Guid? revertBatchId;
        using (JournalFeedScope.Begin(request.ScopeKind, request.ScopeId, "revert"))
        using (journal.BeginBatch(request.Label, request.ScopeKind, request.ScopeId))
        {
            await db.SaveChangesAsync(cancellationToken);
            revertBatchId = journal.Current?.BatchId;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChangeJournalRevertResult(true, first, revertBatchId, [], null);
    }

    private static async Task<ChangeJournalConflict?> RevertWholeKeyAsync(
        BlocwerkDbContext db, List<ChangeJournalEntry> ordered, Func<string, byte[]?> resolveBlob, CancellationToken ct)
    {
        if (await CheckKeyDivergenceAsync(db, ordered, resolveBlob, ct) is { } conflict)
        {
            return conflict;
        }

        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            await ApplyInverseAsync(db, ordered[i], resolveBlob, ct);
        }

        return null;
    }

    /// <summary>The owned columns must match and are restored; the batch's other columns are restored where untouched since.</summary>
    private static async Task<ChangeJournalConflict?> RevertOwnedAsync(
        BlocwerkDbContext db, List<ChangeJournalEntry> ordered, IReadOnlySet<string> owned, Func<string, byte[]?> resolveBlob, CancellationToken ct)
    {
        var last = ordered[^1];
        var (clrType, entityType) = ResolveOrNull(db, last);
        if (clrType is null || entityType is null || ordered.Any(e => e.Op != ChangeJournalOp.Update))
        {
            return Conflict(last, "Only plain updates of a known row can be taken back one column set at a time.");
        }

        var image = ChangeJournalKeyNetEffect.Reduce(entityType, ordered.Select(e => (e.Op, e.BeforeJson, e.AfterJson)).ToList(), resolveBlob).NetFinalImage ?? [];
        if (await ChangeJournalEntityAccessor.FindAsync(db, clrType, entityType, last.KeyJson, ct) is not { } found)
        {
            return Conflict(last, "Row expected present (journal recorded a write) but is absent.");
        }

        var row = db.Entry(found);
        var ownedImage = image.Where(p => owned.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (!ChangeJournalEntityAccessor.CurrentMatches(row, ownedImage, out var detail))
        {
            return Conflict(last, $"Current state no longer matches the batch's net effect: {detail}.");
        }

        var restore = image.Keys
            .Where(k => owned.Contains(k) || ChangeJournalEntityAccessor.CurrentMatches(row, new Dictionary<string, object?> { [k] = image[k] }, out _))
            .ToHashSet(StringComparer.Ordinal);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var before = ChangeJournalValueWriter.DeserializeValues(entityType, ordered[i].BeforeJson ?? "{}", resolveBlob);
            ChangeJournalEntityAccessor.ApplyValues(row, before.Where(p => restore.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
        }

        return null;
    }
}

/// <summary>What <see cref="ChangeJournalReverter.RevertKeyAsync"/> takes back.</summary>
/// <param name="BatchIdsNewestFirst">The batches whose entries for the row are reverted, the newest first.</param>
/// <param name="EntityType">The entity type name as journalled (e.g. "Hold").</param>
/// <param name="KeyJson">The row's key as journalled.</param>
/// <param name="Label">The label of the revert's own batch.</param>
/// <param name="ScopeKind">Its scope kind.</param>
/// <param name="ScopeId">Its scope id.</param>
/// <param name="ActingUserId">Who reverts (the revert batch's actor).</param>
/// <param name="OwnedProperties">The columns the revert is about (see the method's remarks), or null for all.</param>
public sealed record ChangeJournalKeyRevert(
    IReadOnlyList<Guid> BatchIdsNewestFirst,
    string EntityType,
    string KeyJson,
    string Label,
    ChangeJournalScopeKind ScopeKind,
    Guid? ScopeId,
    Guid ActingUserId,
    IReadOnlySet<string>? OwnedProperties = null);
