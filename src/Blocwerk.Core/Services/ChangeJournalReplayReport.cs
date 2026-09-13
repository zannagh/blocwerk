namespace Blocwerk.Core.Services;

/// <summary>What replaying one package entry onto the target did (or would do, on a dry run).</summary>
public enum ChangeJournalReplayOutcome
{
    /// <summary>The target matched the entry's before-image and the after-image was (or would be) applied.</summary>
    Applied = 0,

    /// <summary>The target already held the entry's after-image, so nothing needed to change.</summary>
    SkippedAlreadyApplied = 1,

    /// <summary>The target diverged from the entry's before-image; it was left untouched.</summary>
    Conflict = 2,
}

/// <summary>Per-row outcome of a replay evaluation.</summary>
public sealed record ChangeJournalReplayRow(
    int Seq,
    string EntityType,
    string KeyJson,
    ChangeJournalReplayOutcome Outcome,
    string? Detail);

/// <summary>
/// The result of <see cref="ChangeJournalReplayer.ImportReplayAsync"/>. <see cref="Applied"/> is true
/// only when a non-dry run committed. <see cref="FatalErrors"/> holds fail-fast reasons (schema
/// parity / missing scope) that stopped the replay before (or instead of) applying. On a committed
/// run, <see cref="ReplayBatchId"/> is the NEW batch the interceptor recorded for the replayed writes.
/// </summary>
public sealed record ChangeJournalReplayReport(
    bool Applied,
    bool DryRun,
    IReadOnlyList<ChangeJournalReplayRow> Rows,
    IReadOnlyList<string> FatalErrors,
    Guid? ReplayBatchId);
