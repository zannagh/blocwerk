// <copyright file="ChangeJournalAction.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// One audited action's journal batch, opened by <see cref="IChangeJournal.BeginAction"/>: every journalled write on
/// the current async flow attaches to it until it is disposed, and <see cref="CompleteAsync"/> makes sure the batch row
/// exists once the action succeeded, so the audit trail also covers actions whose own writes are not journalled.
/// </summary>
public sealed class ChangeJournalAction : IDisposable
{
    private readonly ChangeJournalBatchScope scope;
    private readonly Func<ChangeJournalBatchScope, string?, Task> record;

    internal ChangeJournalAction(ChangeJournalBatchScope scope, Func<ChangeJournalBatchScope, string?, Task> record)
    {
        this.scope = scope;
        this.record = record;
    }

    /// <summary>The batch id the action's journal rows (and its audit row) carry.</summary>
    public Guid BatchId => scope.BatchId;

    /// <summary>
    /// Records the action as done by <paramref name="actor"/>: a no-op when a journalled write already created the
    /// batch row (it then carries the writing context's user), otherwise an entry-less, sealed batch row.
    /// </summary>
    public Task CompleteAsync(string? actor)
    {
        return scope.BatchRowCreated ? Task.CompletedTask : record(scope, actor);
    }

    public void Dispose()
    {
        scope.Dispose();
    }
}
