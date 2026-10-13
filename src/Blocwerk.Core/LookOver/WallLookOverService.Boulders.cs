// <copyright file="WallLookOverService.Boulders.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// What settling a finding does to boulders, and what undoing it puts back. "Still there" and "Same hold" say the
/// update's reason was wrong: the hold's "not found again" rows are settled the way a 3D re-measurement settles a move
/// (<see cref="BoulderHoldMove.RemeasuredOutcome"/> = stayed), and <see cref="HoldMoveRemeasurer"/>'s own rule then
/// clears a boulder that has no reason left. It errs on the side of leaving a boulder marked: only a boulder whose
/// mark this update explained with one "not found again" hold and nothing else
/// (<see cref="WallLookOverFinding.MarkedBoulderIds"/>) is ever cleared, never one with another open finding, and
/// never one whose holds were merged as duplicates since (that marks a boulder without leaving a reason).
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>Settles the hold's "not found again" rows of this update and clears the boulders on it that have nothing else left.</summary>
    private async Task<List<Guid>> WithdrawReasonAsync(BlocwerkDbContext db, WallLookOverFinding finding, Guid holdId, CancellationToken ct)
    {
        var rows = await db.BoulderHoldMoves
            .Where(m => m.NewHoldId == holdId && m.ToGeneration == finding.Generation && m.Outcome == HoldMoveOutcome.NotFound && m.RemeasuredOutcome == null)
            .ToListAsync(ct);
        rows.ForEach(r => r.RemeasuredOutcome = HoldMoveOutcome.Stayed);
        await db.SaveChangesAsync(ct);

        // Any boulder on this hold whose mark the update explained, by this finding or by another one settled before:
        // this answer may be the last thing that kept it marked.
        var explained = await ExplainedBouldersAsync(db, finding, f => f.MarkedBoulderIds, ct);
        var candidates = await db.BoulderHolds.Where(bh => bh.HoldId == holdId && explained.Contains(bh.BoulderId)).Select(bh => bh.BoulderId).ToListAsync(ct);
        return await HoldMoveRemeasurer.ClearSettledBouldersAsync(
            db,
            candidates,
            finding.Generation,
            logger,
            async boulderId => await HoldMoveRemeasurer.HasOpenFindingAsync(db, finding.WallId, boulderId, finding.Id, ct)
                || await MergedSinceAsync(db, finding, boulderId, ct));
    }

    /// <summary>
    /// Puts back what settling took off, on the live hold only: the hold's mark and its kept-unconfirmed state, its "not found again" reason, and the
    /// mark of every boulder on it that a look-over answer cleared — by this answer or by a later one on another of
    /// the boulder's holds, which is where the clearing is recorded when this finding was not the last to be settled.
    /// </summary>
    private static async Task RestoreMarksAsync(BlocwerkDbContext db, WallLookOverFinding finding, Guid? liveHoldId, CancellationToken ct)
    {
        if (liveHoldId is not { } holdId)
        {
            return;
        }

        if ((finding.ClearedHoldMark || finding.ClearedKept) && await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId, ct) is { } hold)
        {
            hold.NeedsReview |= finding.ClearedHoldMark;
            hold.KeptUnconfirmedGeneration = finding.ClearedKept ? finding.Generation : hold.KeptUnconfirmedGeneration;
        }

        if (!LookOverRules.SettlingWithdrawsTheReason(finding.Kind))
        {
            return;
        }

        var rows = await db.BoulderHoldMoves
            .Where(m => m.NewHoldId == holdId && m.ToGeneration == finding.Generation && m.Outcome == HoldMoveOutcome.NotFound
                && m.RemeasuredOutcome == HoldMoveOutcome.Stayed && m.RemeasuredDistanceMm == null)
            .ToListAsync(ct);
        rows.ForEach(r => r.RemeasuredOutcome = null);
        var cleared = await ExplainedBouldersAsync(db, finding, f => f.ClearedBoulderIds, ct);
        var boulders = await db.Boulders
            .Where(b => cleared.Contains(b.Id) && !b.NeedsReview && !b.IsArchived && !b.IsHistoric && db.BoulderHolds.Any(bh => bh.BoulderId == b.Id && bh.HoldId == holdId))
            .ToListAsync(ct);
        boulders.ForEach(b => b.NeedsReview = true);
    }

    /// <summary>The boulders named by the findings of the same update (this one included), in one of their two lists.</summary>
    private static async Task<HashSet<Guid>> ExplainedBouldersAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, Func<WallLookOverFinding, List<Guid>?> list, CancellationToken ct)
    {
        var sameUpdate = await db.WallLookOverFindings.AsNoTracking()
            .Where(f => f.WallId == finding.WallId && f.Generation == finding.Generation && f.Id != finding.Id && f.Kind != LookOverFindingKind.DiscardedDetection)
            .ToListAsync(ct);
        return sameUpdate.Append(finding).SelectMany(f => list(f) ?? []).ToHashSet();
    }

    /// <summary>
    /// A duplicate merge since the update touched the boulder (it moved a membership of it, or marked it): such a merge
    /// can mark a boulder for using both holds and leaves no reason row, so the boulder is not cleared from here.
    /// </summary>
    private static async Task<bool> MergedSinceAsync(BlocwerkDbContext db, WallLookOverFinding finding, Guid boulderId, CancellationToken ct)
    {
        var id = boulderId.ToString();
        return await db.ChangeJournalEntries.AnyAsync(
            e => (e.EntityType == nameof(Boulder) || e.EntityType == nameof(BoulderHold)) && e.KeyJson.Contains(id)
                && db.ChangeJournalBatches.Any(b => b.Id == e.BatchId && b.Label == HoldDuplicateService.BatchLabel && b.ScopeId == finding.WallId
                    && b.Status == ChangeJournalStatus.Recorded && b.CreatedAt >= finding.CreatedAt),
            ct);
    }
}
