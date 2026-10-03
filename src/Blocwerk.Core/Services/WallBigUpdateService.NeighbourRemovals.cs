// <copyright file="WallBigUpdateService.NeighbourRemovals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The "Delete hold" choices the overlap step records on a re-shot neighbour panel, applied at promote.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// The holds the promote may delete for one overlap step: the user's "Delete hold" choices, minus every
    /// staged hold the carry already made an old hold's successor (a re-found twin, on the centre or on this
    /// panel). By the time removals run the carry has repointed boulder memberships and lineage onto those
    /// twins; deleting one either failed the whole promote (the pending memberships still point at it) or
    /// quietly retired a hold the carry reported as carried. Same rule as the centre reconcile, which skips
    /// consumed twins: the carry wins. A kept twin is flagged <see cref="Hold.NeedsReview"/> and the override
    /// is logged with both ids (holds have no review-reason field), so the owner sees the conflicting delete
    /// and can remove it on the live wall, where the delete path handles its boulders. Holds the carry did not
    /// use — including new centre holds kept by the review — are deleted as before.
    /// </summary>
    private HashSet<Guid> RemovableNeighbourHoldIds(BlocwerkDbContext db, Guid wallId, NeighbourLinkSet linkSet)
    {
        var removed = linkSet.RemovedNeighbourHoldIds.ToHashSet();
        var successorOf = CollectSuccessors(db, wallId)
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);
        foreach (var id in removed.Where(successorOf.ContainsKey).ToList())
        {
            removed.Remove(id);
            if (db.Holds.Local.FirstOrDefault(h => h.Id == id) is { } twin)
            {
                twin.NeedsReview = true;
            }

            logger.LogWarning(
                "Big update on wall {WallId}: kept hold {HoldId} although the overlap step on panel {PanelId} marked it deleted, because it is the re-found successor of hold {OldHoldId}; flagged for review",
                wallId, id, linkSet.PanelId, successorOf[id]);
        }

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
