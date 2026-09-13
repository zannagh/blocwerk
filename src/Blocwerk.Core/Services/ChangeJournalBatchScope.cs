using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The mutable ambient state for one open <see cref="IChangeJournal.BeginBatch"/> scope. Holds the
/// batch identity, whether its row has been persisted yet, and the running sequence counter that is
/// carried across every SaveChanges the scope spans. Created and owned by <see cref="ChangeJournal"/>;
/// read by the journal interceptor.
/// </summary>
internal sealed class ChangeJournalBatchScope : IDisposable
{
    private readonly Action onDispose;

    public ChangeJournalBatchScope(
        string label,
        ChangeJournalScopeKind scopeKind,
        Guid? scopeId,
        Action onDispose,
        Guid? batchId = null,
        int startSeq = 0,
        bool batchRowCreated = false)
    {
        Label = label;
        ScopeKind = scopeKind;
        ScopeId = scopeId;
        this.onDispose = onDispose;
        BatchId = batchId ?? Guid.NewGuid();
        NextSeq = startSeq;
        BatchRowCreated = batchRowCreated;
    }

    public Guid BatchId { get; }

    public string Label { get; }

    public ChangeJournalScopeKind ScopeKind { get; }

    public Guid? ScopeId { get; }

    /// <summary>Set once the <c>ChangeJournalBatch</c> row has been added, so later saves don't re-add it.</summary>
    public bool BatchRowCreated { get; set; }

    /// <summary>The next <c>Seq</c> value to assign, continued across every SaveChanges in the scope.</summary>
    public int NextSeq { get; set; }

    public void Dispose()
    {
        onDispose();
    }
}
