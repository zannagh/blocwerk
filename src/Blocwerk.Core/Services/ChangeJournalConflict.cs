namespace Blocwerk.Core.Services;

/// <summary>
/// One row whose current database state did not match what the journal expected, so it blocked a
/// revert (or a non-forced replay). Purely diagnostic: it names the row and why it diverged.
/// </summary>
public sealed record ChangeJournalConflict(int Seq, string EntityType, string KeyJson, string Reason);
