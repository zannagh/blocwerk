using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// The one place that prepares holds for a hard delete. Three FK families reference
/// <see cref="Hold"/> with <c>Restrict</c>, so every one of them has to be dealt with in the SAME
/// SaveChanges as the delete or the whole unit of work (the change journal included) rolls back
/// (and, without a key, what sits ON a dying hold is un-stacked: it stays, as a hold on the wall):
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
    /// <param name="retired">When given, receives each boulder this flagged historic (for a caller that names them).</param>
    public static async Task<int> PrepareHoldsForDeleteAsync(
        BlocwerkDbContext db,
        IReadOnlyCollection<Guid> holdIds,
        HoldDeleteBoulderPolicy boulderPolicy = HoldDeleteBoulderPolicy.DetachAndFlagHistoric,
        bool clearNeedsReview = false,
        CancellationToken ct = default,
        ICollection<Boulder>? retired = null)
    {
        if (holdIds.Count == 0)
        {
            return 0;
        }

        var ids = holdIds.Distinct().ToList();
        var historic = 0;
        if (boulderPolicy == HoldDeleteBoulderPolicy.DetachAndFlagHistoric)
        {
            historic = await DetachBoulderHoldsAsync(db, ids, clearNeedsReview, retired, ct);
        }

        await RemoveHoldLinksAsync(db, ids, ct);
        await TombstoneGenerationLinksAsync(db, ids, ct);
        await UnstackAsync(db, ids, ct);
        return historic;
    }

    /// <summary>Convenience overload for the single-hold call sites.</summary>
    public static Task<int> PrepareHoldForDeleteAsync(
        BlocwerkDbContext db,
        Guid holdId,
        HoldDeleteBoulderPolicy boulderPolicy = HoldDeleteBoulderPolicy.DetachAndFlagHistoric,
        bool clearNeedsReview = false,
        CancellationToken ct = default,
        ICollection<Boulder>? retired = null)
    {
        return PrepareHoldsForDeleteAsync(db, new[] { holdId }, boulderPolicy, clearNeedsReview, ct, retired);
    }

    /// <summary>
    /// The holds that sit on a dying hold (<see cref="Hold.StackedOnHoldId"/>) stay, as holds on the wall. Tracked, so
    /// the change journal records it and a revert that brings the base back stacks them again.
    /// </summary>
    /// <param name="db">The context; the caller commits.</param>
    /// <param name="ids">The holds about to be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task UnstackAsync(BlocwerkDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var bases = ids.Select(id => (Guid?)id).ToList();

        // By what the context holds now: a merge has already moved these holds onto the hold that stays.
        foreach (var upper in (await db.Holds.Where(h => bases.Contains(h.StackedOnHoldId)).ToListAsync(ct)).Where(h => bases.Contains(h.StackedOnHoldId)))
        {
            upper.StackedOnHoldId = null;
        }
    }

    private static async Task<int> DetachBoulderHoldsAsync(
        BlocwerkDbContext db, List<Guid> ids, bool clearNeedsReview, ICollection<Boulder>? retired, CancellationToken ct)
    {
        // A membership the caller already took off the hold in this unit of work (moved onto another hold) is not on
        // the dying hold any more, and its boulder is not retired with it.
        var memberships = (await db.BoulderHolds
                .Include(bh => bh.Boulder)
                .Where(bh => ids.Contains(bh.HoldId))
                .ToListAsync(ct))
            .Where(bh => db.Entry(bh).State != EntityState.Deleted)
            .ToList();

        var historic = 0;
        foreach (var membership in memberships)
        {
            if (membership.Boulder is { IsArchived: false, IsHistoric: false })
            {
                membership.Boulder.IsHistoric = true;
                historic++;
                retired?.Add(membership.Boulder);

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
        // By what the context holds now, as in UnstackAsync: a merge has already re-pointed some of these onto the hold
        // that stays (HoldRelinking), and those must survive.
        var links = (await db.HoldLinks
                .Where(l => ids.Contains(l.HoldAId) || ids.Contains(l.HoldBId))
                .ToListAsync(ct))
            .Where(l => ids.Contains(l.HoldAId) || ids.Contains(l.HoldBId))
            .ToList();
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
