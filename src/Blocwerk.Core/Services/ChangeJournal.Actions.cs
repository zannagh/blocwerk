// <copyright file="ChangeJournal.Actions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>Audited actions: a batch row written before the action, completed or removed after it.</summary>
public sealed partial class ChangeJournal
{
    /// <inheritdoc/>
    public async Task<ChangeJournalAction> StartActionAsync(
        string label, ChangeJournalScopeKind scopeKind, Guid? scopeId, string? actor, bool append = false)
    {
        await using var db = RequireRegistryContext();
        var existing = append
            ? await db.ChangeJournalBatches.FirstOrDefaultAsync(b =>
                b.Label == label && b.ScopeKind == scopeKind && b.ScopeId == scopeId
                && (b.Status == ChangeJournalStatus.Pending || b.Status == ChangeJournalStatus.Recorded))
            : null;
        if (existing is not null)
        {
            var maxSeq = await db.ChangeJournalEntries.Where(e => e.BatchId == existing.Id).Select(e => (int?)e.Seq).MaxAsync();
            return new ChangeJournalAction(this, existing.Id, label, scopeKind, scopeId, (maxSeq ?? -1) + 1, created: false, append);
        }

        var batch = new ChangeJournalBatch
        {
            Label = label,
            ScopeKind = scopeKind,
            ScopeId = scopeId,
            Actor = actor,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = ChangeJournalStatus.Pending,
        };
        db.ChangeJournalBatches.Add(batch);
        await db.SaveChangesAsync();
        return new ChangeJournalAction(this, batch.Id, label, scopeKind, scopeId, 0, created: true, append);
    }

    /// <summary>Makes the action's batch the ambient one (its row exists already, so the interceptor only adds entries).</summary>
    internal IDisposable EnterAction(ChangeJournalAction action) =>
        Enter(action.Label, action.ScopeKind, action.ScopeId, action.BatchId, action.StartSeq, batchRowCreated: true);

    /// <summary>Records the action: Pending becomes Recorded (sealed unless it is an appended batch).</summary>
    internal async Task CompleteActionAsync(ChangeJournalAction action)
    {
        await using var db = RequireRegistryContext();
        var batch = await db.ChangeJournalBatches.FirstOrDefaultAsync(b => b.Id == action.BatchId);
        if (batch is null || batch.Status != ChangeJournalStatus.Pending)
        {
            return;
        }

        batch.Status = ChangeJournalStatus.Recorded;
        batch.SealedAt = action.Append ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The action was refused or failed: a batch it created goes away when nothing was journalled in it, and is kept as
    /// Failed (sealed) otherwise. An appended batch someone else created is left alone.
    /// </summary>
    internal async Task FailActionAsync(ChangeJournalAction action)
    {
        if (!action.Created)
        {
            return;
        }

        await using var db = RequireRegistryContext();
        var batch = await db.ChangeJournalBatches.FirstOrDefaultAsync(b => b.Id == action.BatchId);
        if (batch is null)
        {
            return;
        }

        if (await db.ChangeJournalEntries.AnyAsync(e => e.BatchId == action.BatchId))
        {
            batch.Status = ChangeJournalStatus.Failed;
            batch.SealedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.ChangeJournalBatches.Remove(batch);
        }

        await db.SaveChangesAsync();
    }
}
