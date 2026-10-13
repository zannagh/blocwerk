// <copyright file="LookOverReconciler.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// Brings the open findings in line with what was done in the hold editor since the update, so the wall can be edited
/// first and looked over once: a finding whose hold was removed, merged or checked there is closed with that reason
/// (<see cref="LookOverFindingStatus.Closed"/>), one whose "new hold on the spot" is gone forgets that hold, and a
/// hold placed by hand on the spot of a hold the update could not find becomes that finding's "new hold on the spot"
/// (so the look-over asks whether it is the same hold).
/// Run whenever the state or the list is read; it only ever reads what is, so running it again changes nothing.
/// Who made the edit is not known here: the reason and the time are recorded.
/// </summary>
internal static class LookOverReconciler
{
    /// <summary>Closes what the hold editor has dealt with and saves; returns how many findings changed.</summary>
    /// <param name="db">The context (saved here when anything changed).</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of findings closed or updated.</returns>
    public static async Task<int> ReconcileAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        // A discarded detection is about no hold until it is added, so no edit in the hold editor closes it.
        var open = await db.WallLookOverFindings
            .Where(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open && f.Kind != LookOverFindingKind.DiscardedDetection)
            .ToListAsync(ct);
        if (open.Count == 0)
        {
            return 0;
        }

        var ids = open.SelectMany(f => new[] { f.HoldId, f.OtherHoldId }).OfType<Guid>().Distinct().ToList();
        var holds = await db.Holds.AsNoTracking().Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var placed = await PlacedByHandAsync(db, wallId, ct);
        var changed = 0;
        foreach (var finding in open)
        {
            var before = (finding.Status, finding.OtherHoldId);
            if (finding.OtherHoldId is { } other && !holds.ContainsKey(other))
            {
                ForgetNewHold(finding);
            }

            if (finding.HoldId is not { } holdId)
            {
                if (finding.Kind == LookOverFindingKind.ConflictingNew && await HoldAddedAsync(db, finding, ct))
                {
                    Close(finding, LookOverCloseReason.HoldAddedHere);
                }
            }
            else if (!holds.TryGetValue(holdId, out var hold))
            {
                Close(finding, await MergedAsync(db, wallId, holdId, ct) ? LookOverCloseReason.MergedInEditor : LookOverCloseReason.RemovedInEditor);
            }
            else if ((finding.HoldKept && hold.KeptUnconfirmedGeneration is null) || (finding.FlaggedHold && !hold.NeedsReview))
            {
                // The state the update gave the hold for this finding is gone, and not by an answer here: the editor took it off.
                Close(finding, LookOverCloseReason.CheckedInEditor);
            }
            else if (finding is { HoldKept: true, OtherHoldId: null }
                && SameSpotFinder.Nearest(hold, placed.Where(p => p.WallPanelId == hold.WallPanelId && p.Generation == hold.Generation), null) is { } added)
            {
                (finding.OtherHoldId, finding.Covered) = (added.Id, SameSpotFinder.Covers(added, hold, null));
            }

            changed += before == (finding.Status, finding.OtherHoldId) ? 0 : 1;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    private static void Close(WallLookOverFinding finding, LookOverCloseReason reason) =>
        (finding.Status, finding.ClosedReason, finding.ClosedAt) = (LookOverFindingStatus.Closed, reason, DateTimeOffset.UtcNow);

    /// <summary>
    /// The new hold on the spot was removed in the editor. The old hold is still a copy nobody confirmed, so its
    /// finding stays open: what remains to answer is whether the old hold is there, which is "not found again".
    /// </summary>
    private static void ForgetNewHold(WallLookOverFinding finding)
    {
        (finding.OtherHoldId, finding.Covered) = (null, false);
        if (finding.Kind == LookOverFindingKind.SameSpot)
        {
            finding.Kind = LookOverFindingKind.NotFound;
        }
    }

    /// <summary>
    /// The wall's holds a person placed since the update: not detected, not carried from the generation before, and
    /// not themselves a copy nobody confirmed.
    /// </summary>
    private static Task<List<Hold>> PlacedByHandAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        db.Holds.AsNoTracking()
            .Where(h => h.WallId == wallId && !h.IsAutoDetected && !h.IsVirtual && h.KeptUnconfirmedGeneration == null && h.WallPanelId != null
                && db.Walls.Any(w => w.Id == wallId && w.CurrentGeneration == h.Generation)
                && !db.HoldGenerationLinks.Any(l => l.NewHoldId == h.Id))
            .OrderBy(h => h.Id)
            .ToListAsync(ct);

    /// <summary>The hold went in a merge of the duplicate-holds review (the journal says which batch deleted it).</summary>
    private static async Task<bool> MergedAsync(BlocwerkDbContext db, Guid wallId, Guid holdId, CancellationToken ct)
    {
        var id = holdId.ToString();
        return await db.ChangeJournalEntries.AnyAsync(
            e => e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Delete && e.KeyJson.Contains(id)
                && db.ChangeJournalBatches.Any(b => b.Id == e.BatchId && b.Label == HoldDuplicateService.BatchLabel && b.ScopeId == wallId),
            ct);
    }

    /// <summary>
    /// A hold was placed by hand where the detection was left out: on the finding's photo, at its spot, not detected
    /// and not carried from the generation before (those were there when the update decided to leave the detection out).
    /// </summary>
    private static async Task<bool> HoldAddedAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        if (finding.PanelId is not { } panelId)
        {
            return false;
        }

        var placed = await db.Holds.AsNoTracking()
            .Where(h => h.WallPanelId == panelId && h.Generation == finding.Generation && !h.IsAutoDetected && !h.IsVirtual
                && !db.HoldGenerationLinks.Any(l => l.NewHoldId == h.Id))
            .Select(h => new { h.X, h.Y, h.Radius })
            .ToListAsync(ct);
        return placed.Any(h =>
        {
            var (dx, dy) = (h.X - finding.X, h.Y - finding.Y);
            return Math.Sqrt((dx * dx) + (dy * dy)) <= Math.Max(h.Radius, finding.Radius);
        });
    }
}
