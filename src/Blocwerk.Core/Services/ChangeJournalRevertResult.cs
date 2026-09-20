using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The outcome of <see cref="ChangeJournalReverter.RevertBatchAsync"/>. When <see cref="Reverted"/>
/// is false the transaction was rolled back and nothing changed; <see cref="Outcome"/> says why in a
/// form the UI can branch on, with <see cref="Conflicts"/> (or <see cref="Error"/>) as the detail. On
/// success, <see cref="RevertBatchId"/> is the NEW batch the interceptor recorded for the revert's own
/// inverse writes, so a revert is itself replayable.
/// </summary>
public sealed record ChangeJournalRevertResult(
    bool Reverted,
    Guid OriginalBatchId,
    Guid? RevertBatchId,
    IReadOnlyList<ChangeJournalConflict> Conflicts,
    string? Error,
    ChangeJournalRevertOutcome Outcome);
