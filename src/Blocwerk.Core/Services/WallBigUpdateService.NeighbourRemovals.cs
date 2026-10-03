// <copyright file="WallBigUpdateService.NeighbourRemovals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The "Delete hold" choices the overlap step records on a re-shot neighbour panel, applied at promote.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// The neighbour detections the promote may delete for one panel: the user's removals, minus every
    /// detection the carry already made an old hold's successor. The overlap step lists those re-found
    /// twins like any other detection, but by the time removals run the carry has repointed boulder
    /// memberships and lineage onto them; deleting one then either failed the whole promote (the pending
    /// memberships still point at it) or quietly retired a hold the carry reported as carried. Same rule as
    /// the centre reconcile, which skips consumed twins: the carry wins. A kept twin is flagged
    /// <see cref="Hold.NeedsReview"/> so the owner sees the conflicting delete and can remove it on the live
    /// wall, where the delete path handles its boulders.
    /// </summary>
    private static HashSet<Guid> RemovableNeighbourHoldIds(
        NeighbourLinkSet linkSet,
        IReadOnlyList<Hold> stagedHolds,
        IReadOnlySet<Guid> survivingCenterStaged)
    {
        var removed = linkSet.RemovedNeighbourHoldIds.ToHashSet();
        foreach (var hold in stagedHolds)
        {
            if (removed.Contains(hold.Id) && survivingCenterStaged.Contains(hold.Id))
            {
                removed.Remove(hold.Id);
                hold.NeedsReview = true;
            }
        }

        removed.RemoveWhere(survivingCenterStaged.Contains);
        return removed;
    }

    /// <summary>
    /// Deletes holds a boulder may point at: clears everything referencing them with a Restrict FK
    /// (memberships — their boulders go historic — panel links, and the cross-generation lineage,
    /// which is tombstoned rather than dropped), then removes the holds. One set-based preparation for
    /// the whole batch rather than three queries per hold, since this runs inside the promote
    /// transaction. No SaveChanges.
    /// </summary>
    private static async Task DeleteHoldsAsync(BlocwerkDbContext db, Guid wallId, HashSet<Guid> holdIds)
    {
        if (holdIds.Count == 0)
        {
            return;
        }

        var holds = await db.Holds.Where(h => holdIds.Contains(h.Id) && h.WallId == wallId).ToListAsync();
        await HoldDeletion.PrepareHoldsForDeleteAsync(
            db, holds.Select(h => h.Id).ToList(), clearNeedsReview: true);
        db.Holds.RemoveRange(holds);
    }
}
