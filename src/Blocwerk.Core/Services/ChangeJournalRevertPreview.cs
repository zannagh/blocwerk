using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// What <see cref="ChangeJournalReverter.PreviewRevertAsync"/> found: a DRY RUN of the exact guards the
/// real revert runs, with no transaction opened and nothing written. If <see cref="CanRevert"/> is true
/// the revert is expected to succeed — expected, not guaranteed: the state can still change between the
/// preview and the revert, which is why the revert re-runs the same guards itself.
/// </summary>
/// <param name="BatchId">The batch that was previewed.</param>
/// <param name="Found">False when no batch has that id; every other field is then at its default.</param>
/// <param name="Label">The batch label.</param>
/// <param name="Status">The batch's current status. Anything but Recorded means the revert will refuse.</param>
/// <param name="ScopeKind">Wall / Boulder / None.</param>
/// <param name="ScopeId">The scoped aggregate's id, or null.</param>
/// <param name="EntryCount">How many journal entries the revert would invert.</param>
/// <param name="Conflicts">
/// The precondition conflicts, with their keys resolved for display. Empty means both guards passed.
/// </param>
/// <param name="CanRevert">
/// True when the batch exists, is still Recorded, and has no conflicts. False means an unforced revert
/// will change nothing.
/// </param>
/// <param name="RequiresWallUpdateSessionCleanup">
/// True when this is a wall-update batch AND the wall still has an Open
/// <see cref="Entities.WallUpdateSession"/>. That header is NOT journalled, so reverting the batch
/// deletes the staged panels and holds under it but leaves the session behind as an empty Open session,
/// which then blocks the next staging on that wall. The reverter does not clean it up by design; tell
/// the operator that a follow-up ("Discard theirs &amp; start over" in the update wizard, which in this
/// state destroys nothing) is needed, and offer <see cref="WallUpdateSessionId"/> as the handle.
/// </param>
/// <param name="WallUpdateSessionId">The Open session that would be orphaned, or null when there is none.</param>
public sealed record ChangeJournalRevertPreview(
    Guid BatchId,
    bool Found,
    string Label,
    ChangeJournalStatus Status,
    ChangeJournalScopeKind ScopeKind,
    Guid? ScopeId,
    int EntryCount,
    IReadOnlyList<ChangeJournalConflictInfo> Conflicts,
    bool CanRevert,
    bool RequiresWallUpdateSessionCleanup,
    Guid? WallUpdateSessionId);
