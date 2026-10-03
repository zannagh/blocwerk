// <copyright file="ChangeJournalAction.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// One audited action's journal batch, started by <see cref="IChangeJournal.StartActionAsync"/> with its row already
/// written (Pending). <see cref="Enter"/> makes it the ambient batch for the action's writes; afterwards
/// <see cref="CompleteAsync"/> records it, or <see cref="FailAsync"/> takes it back.
/// </summary>
public sealed class ChangeJournalAction
{
    private readonly ChangeJournal journal;

    internal ChangeJournalAction(
        ChangeJournal journal, Guid batchId, string label, ChangeJournalScopeKind scopeKind, Guid? scopeId, int startSeq, bool created, bool append)
    {
        this.journal = journal;
        BatchId = batchId;
        Label = label;
        ScopeKind = scopeKind;
        ScopeId = scopeId;
        StartSeq = startSeq;
        Created = created;
        Append = append;
    }

    /// <summary>The batch the action's audit row and journal rows belong to.</summary>
    public Guid BatchId { get; }

    /// <summary>True when this action created the batch row (false: appended to an existing one).</summary>
    public bool Created { get; }

    internal string Label { get; }

    internal ChangeJournalScopeKind ScopeKind { get; }

    internal Guid? ScopeId { get; }

    internal int StartSeq { get; }

    internal bool Append { get; }

    /// <summary>
    /// Makes the batch the ambient one on the current async flow until the result is disposed. Call it synchronously
    /// in the method that runs the write, so the ambient batch flows into it.
    /// </summary>
    public IDisposable Enter() => journal.EnterAction(this);

    /// <summary>The action succeeded: its row is recorded.</summary>
    public Task CompleteAsync() => journal.CompleteActionAsync(this);

    /// <summary>The action was refused or failed: its row is removed, or kept as failed when rows were journalled.</summary>
    public Task FailAsync() => journal.FailActionAsync(this);
}
