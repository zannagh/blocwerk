// <copyright file="WallBigUpdateService.Repoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Moving the boulders that use a carried old hold onto its successor at promote.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Re-points the memberships on THIS old hold onto its new-generation successor row, for every active
    /// (non-archived, non-historic) boulder that used it. Only ever called for an old hold on a
    /// re-photographed panel (the carry scopes <c>oldHolds</c> to updated panels), so it advances exactly
    /// the memberships whose hold is being re-shot and never touches a membership on a non-updated panel.
    /// <para>
    /// Subset promote (decision D-B, corrected): a boulder WHOLLY on updated panels has every membership
    /// repointed here (full advance); a boulder SPANNING an updated and a non-updated panel is PARTIALLY
    /// repointed — this call advances its updated-panel membership to the successor while its non-updated
    /// membership, whose hold is never passed to this method, stays on the retained gen-N row. That is the
    /// invariant the live read needs: each membership resolves at ITS panel's live generation, so no hold
    /// vanishes from a spanning boulder after a partial promote. A boulder wholly on non-updated panels is
    /// never reached (none of its holds is in the carry set) and stays entirely untouched.
    /// </para>
    /// <para>
    /// Because <see cref="BoulderHold.HoldId"/> is part of the key it cannot be mutated on a tracked row —
    /// the re-point is a delete + insert of the join row, and the insert is idempotent
    /// (<see cref="FindBoulderHold"/>) so a physical merge (two old holds → one new) yields one membership,
    /// keeping the more prominent mark and flagging the boulder for review.
    /// Historic (incl. the Pass-0-frozen) and archived boulders keep their <see cref="BoulderHold"/> on the
    /// retained old row, so their older-gen schematics still render. <paramref name="changed"/> only ever
    /// comes from a Changed carry decision, so a plain carried correction never forces review.
    /// </para>
    /// </summary>
    private static async Task RepointBouldersAsync(
        BlocwerkDbContext db, Guid oldHoldId, Guid newHoldId, int newGen, bool changed)
    {
        var boulderLinks = await db.BoulderHolds
            .Where(bh => bh.HoldId == oldHoldId)
            .Include(bh => bh.Boulder)
            .ToListAsync();

        foreach (var link in boulderLinks)
        {
            if (link.Boulder is not { IsArchived: false, IsHistoric: false })
            {
                continue;
            }

            db.BoulderHolds.Remove(link);
            if (FindBoulderHold(db, link.BoulderId, newHoldId) is { } merged)
            {
                // Two of the boulder's holds became one: keep the more prominent mark (a start or top
                // must not turn into a plain hold) and have a person check the boulder.
                if (link.Type > merged.Type)
                {
                    merged.Type = link.Type;
                }

                link.Boulder.NeedsReview = true;
            }
            else
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = link.BoulderId,
                    HoldId = newHoldId,
                    Type = link.Type,
                    Usage = link.Usage,
                });
            }

            link.Boulder.Generation = newGen;
            if (changed)
            {
                link.Boulder.NeedsReview = true;
            }
        }
    }

    /// <summary>
    /// The live <see cref="BoulderHold"/> for this pair, if one is already present — as a pending insert in
    /// the change tracker (an earlier repoint this transaction) or a committed row. A hit means two old
    /// holds merged onto one successor; the caller then merges into it instead of inserting a duplicate key.
    /// </summary>
    private static BoulderHold? FindBoulderHold(BlocwerkDbContext db, Guid boulderId, Guid holdId)
    {
        var pending = db.BoulderHolds.Local.FirstOrDefault(bh =>
            bh.BoulderId == boulderId && bh.HoldId == holdId
            && db.Entry(bh).State != EntityState.Deleted);
        return pending ?? db.BoulderHolds.FirstOrDefault(bh => bh.BoulderId == boulderId && bh.HoldId == holdId);
    }
}
