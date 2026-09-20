using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// One journal batch rendered for a human: what it was called, what it touched, on which wall/boulder,
/// by whom, and how it sits relative to the other batches on the same scope. Built by
/// <see cref="ChangeJournalBrowser"/>; it never carries the batch's entries.
/// </summary>
/// <param name="BatchId">The batch id to pass to preview/revert.</param>
/// <param name="Label">The batch label, e.g. <c>wall-update</c>, <c>hold-clean-outside-border</c> or <c>adhoc</c>.</param>
/// <param name="CreatedAt">When the batch was opened (UTC).</param>
/// <param name="SealedAt">When it was closed, or null while it is still open and could still grow.</param>
/// <param name="Status">Recorded / Reverted / Replayed — only a Recorded batch can be reverted.</param>
/// <param name="ScopeKind">Whether <paramref name="ScopeId"/> is a wall, a boulder, or nothing.</param>
/// <param name="ScopeId">The scoped aggregate's id, or null for an unscoped batch.</param>
/// <param name="ScopeName">
/// The wall's or boulder's name, resolved by id. Null when the batch is unscoped, or when the scoped
/// row no longer exists (a deleted wall) — render a fallback rather than assuming a name.
/// </param>
/// <param name="ActorUserId">The raw <c>Actor</c> string as journalled (a user id), or null when unattributed.</param>
/// <param name="ActorName">
/// The acting user's display name. Null when <paramref name="ActorUserId"/> is null (a system/unattributed
/// change) or when it does not resolve to a user row — render "system" / "unknown" for both.
/// </param>
/// <param name="Counts">Per (EntityType, Op) aggregate counts, descending by count. Never empty for a batch with entries.</param>
/// <param name="EntryCount">Total journal entries in the batch — the sum of <paramref name="Counts"/>.</param>
/// <param name="NewerBatchesOnScope">
/// How many batches on the SAME scope were created after this one. The reverter has no ordering check,
/// so a non-zero value means reverting this batch skips over later changes: steer the operator to revert
/// newest-first. Always 0 for an unscoped batch.
/// </param>
/// <param name="IsWallUpdateBatch">
/// True when this is the run→promote wall-update batch (label <see cref="ChangeJournal.WallUpdateBatchLabel"/>
/// on a wall scope). Reverting one deletes the journalled panels and holds but leaves the NON-journalled
/// <see cref="Entities.WallUpdateSession"/> header behind as an empty Open session, which then blocks the
/// next staging on that wall. Surface it as "needs follow-up cleanup"; see
/// <see cref="ChangeJournalRevertPreview.RequiresWallUpdateSessionCleanup"/> for the confirmed per-batch answer.
/// </param>
public sealed record ChangeJournalBatchSummary(
    Guid BatchId,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SealedAt,
    ChangeJournalStatus Status,
    ChangeJournalScopeKind ScopeKind,
    Guid? ScopeId,
    string? ScopeName,
    string? ActorUserId,
    string? ActorName,
    IReadOnlyList<ChangeJournalEntityCount> Counts,
    int EntryCount,
    int NewerBatchesOnScope,
    bool IsWallUpdateBatch);
