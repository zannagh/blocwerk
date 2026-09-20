using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// The one place that prepares holds for a hard delete. Three FK families reference
/// <see cref="Hold"/> with <c>Restrict</c>, so every one of them has to be dealt with in the SAME
/// SaveChanges as the delete or the whole unit of work (the change journal included) rolls back:
/// <list type="bullet">
/// <item><see cref="BoulderHold"/> — membership rows, handled per <see cref="HoldDeleteBoulderPolicy"/>.</item>
/// <item><see cref="HoldLink"/> — same physical hold across two panels; meaningless once an end dies, removed.</item>
/// <item><see cref="HoldGenerationLink"/> — cross-generation lineage; TOMBSTONED, not removed (below).</item>
/// </list>
/// <para>
/// Lineage is the one relationship that is not recoverable from anywhere else: the change journal
/// captures a deleted hold's full before-image (so the hold itself can be re-inserted on revert),
/// but the "gen N hold became this gen N+1 hold" statement only lives in these rows. So the dying
/// end is nulled and the row survives, keeping its <c>FromGeneration</c>/<c>ToGeneration</c>/
/// <c>Kind</c>/<c>CreatedAt</c> — the surviving hold still records where it came from, or what
/// became of it.
/// </para>
/// <para>
/// BOTH ends NULL: the row is REMOVED. With neither endpoint it can no longer be traversed from any
/// hold and states nothing but "two unknown holds were once related across these generations" —
/// no reader can use it and every reader has to skip it. It is journalled like any other delete, so
/// a revert that re-inserts both holds can re-insert it too.
/// </para>
/// No SaveChanges and no <c>Holds.Remove</c> of its own: the caller owns both, and must commit the
/// removals in the same transaction as this preparation.
/// </summary>
public static class HoldDeletion
{
    /// <summary>
    /// Clears everything that would block deleting <paramref name="holdIds"/>. Safe for bulk use and
    /// for ids that have no dependants at all. Returns how many active boulders were flagged historic.
    /// </summary>
    /// <param name="db">The context the holds are being deleted on; the caller commits.</param>
    /// <param name="holdIds">The holds about to be removed.</param>
    /// <param name="boulderPolicy">How to treat boulder memberships on those holds.</param>
    /// <param name="clearNeedsReview">
    /// Also clear <see cref="Boulder.NeedsReview"/> on a boulder flagged historic — what the big-update
    /// promote path does, since a boulder it just retired is not awaiting a review any more.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<int> PrepareHoldsForDeleteAsync(
        BlocwerkDbContext db,
        IReadOnlyCollection<Guid> holdIds,
        HoldDeleteBoulderPolicy boulderPolicy = HoldDeleteBoulderPolicy.DetachAndFlagHistoric,
        bool clearNeedsReview = false,
        CancellationToken ct = default)
    {
        if (holdIds.Count == 0)
        {
            return 0;
        }

        var ids = holdIds.Distinct().ToList();
        var historic = 0;
        if (boulderPolicy == HoldDeleteBoulderPolicy.DetachAndFlagHistoric)
        {
            historic = await DetachBoulderHoldsAsync(db, ids, clearNeedsReview, ct);
        }

        await RemoveHoldLinksAsync(db, ids, ct);
        await TombstoneGenerationLinksAsync(db, ids, ct);
        return historic;
    }

    /// <summary>Convenience overload for the single-hold call sites.</summary>
    public static Task<int> PrepareHoldForDeleteAsync(
        BlocwerkDbContext db,
        Guid holdId,
        HoldDeleteBoulderPolicy boulderPolicy = HoldDeleteBoulderPolicy.DetachAndFlagHistoric,
        bool clearNeedsReview = false,
        CancellationToken ct = default)
    {
        return PrepareHoldsForDeleteAsync(db, new[] { holdId }, boulderPolicy, clearNeedsReview, ct);
    }

    private static async Task<int> DetachBoulderHoldsAsync(
        BlocwerkDbContext db, List<Guid> ids, bool clearNeedsReview, CancellationToken ct)
    {
        var memberships = await db.BoulderHolds
            .Include(bh => bh.Boulder)
            .Where(bh => ids.Contains(bh.HoldId))
            .ToListAsync(ct);

        var historic = 0;
        foreach (var membership in memberships)
        {
            if (membership.Boulder is { IsArchived: false, IsHistoric: false })
            {
                membership.Boulder.IsHistoric = true;
                historic++;

                if (clearNeedsReview)
                {
                    membership.Boulder.NeedsReview = false;
                }
            }
        }

        db.BoulderHolds.RemoveRange(memberships);
        return historic;
    }

    private static async Task RemoveHoldLinksAsync(BlocwerkDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var links = await db.HoldLinks
            .Where(l => ids.Contains(l.HoldAId) || ids.Contains(l.HoldBId))
            .ToListAsync(ct);
        if (links.Count > 0)
        {
            db.HoldLinks.RemoveRange(links);
        }
    }

    private static async Task TombstoneGenerationLinksAsync(BlocwerkDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var nullableIds = ids.Select(id => (Guid?)id).ToList();
        var links = await db.HoldGenerationLinks
            .Where(l => nullableIds.Contains(l.OldHoldId) || nullableIds.Contains(l.NewHoldId))
            .ToListAsync(ct);

        var dying = ids.ToHashSet();
        foreach (var link in links)
        {
            if (link.OldHoldId is { } oldId && dying.Contains(oldId))
            {
                link.OldHoldId = null;
            }

            if (link.NewHoldId is { } newId && dying.Contains(newId))
            {
                link.NewHoldId = null;
            }

            if (link.OldHoldId is null && link.NewHoldId is null)
            {
                db.HoldGenerationLinks.Remove(link);
            }
        }
    }
}
