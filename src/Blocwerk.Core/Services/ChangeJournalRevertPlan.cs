using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Everything a revert of one batch needs, gathered once by <see cref="ChangeJournalRevertGuard"/>:
/// the batch's entries in inverse-apply order, the blob resolver their images reference, and the
/// precondition conflicts the guards found. Shared by the dry-run preview and the real revert so the
/// two can never disagree about whether a batch is safe to invert.
/// </summary>
/// <param name="Entries">The batch's entries ordered by DESCENDING Seq — the order the inverse is applied in.</param>
/// <param name="ResolveBlob">Resolves a byte[] column's content hash back to its bytes.</param>
/// <param name="Conflicts">Rows whose current state no longer matches what the batch recorded, or whose
/// deletion would cascade beyond the batch. Empty means the guards passed.</param>
internal sealed record ChangeJournalRevertPlan(
    IReadOnlyList<ChangeJournalEntry> Entries,
    Func<string, byte[]?> ResolveBlob,
    IReadOnlyList<ChangeJournalConflict> Conflicts);
