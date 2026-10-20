// <copyright file="WallLookOverService.NewHold.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Should this boulder use the new hold in that place?", asked per boulder of a hold another hold replaced. Every
/// boulder yes is the duplicate review's merge; every boulder no is "the old hold is gone"; a mix moves the yes
/// boulders' memberships onto the new hold and removes the old hold, which takes the no boulders with it into history
/// (the old hold is physically gone). The mix is one journal batch under the merge's label, so the duplicate review
/// undoes it as one, exactly. A yes boulder whose review mark this replacement alone explained
/// (<see cref="WallLookOverFinding.MarkedBoulderIds"/>) loses the mark: a person just answered for it.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>The refusal when the per-boulder answer names a boulder the card did not show.</summary>
    public const string NotShownBoulder = "Only the boulders that use this hold can be moved onto the new hold. Reload the page and check again.";

    /// <summary>
    /// The boulders that should use the new hold, checked: only with "use the new hold", and only boulders on the hold.
    /// Null when there is no per-boulder answer or every boulder said yes (the plain merge); every boulder no is
    /// <see cref="LookOverAnswer.RemoveHold"/>.
    /// </summary>
    private static (LookOverAnswer Answer, IReadOnlySet<Guid>? OnlyThese) NewHoldChoice(
        LookOverAnswer answer, IReadOnlyCollection<Guid>? newHoldBoulderIds, LookOverFindingView view)
    {
        if (newHoldBoulderIds is null)
        {
            return (answer, null);
        }

        if (answer != LookOverAnswer.UseNewHold)
        {
            throw new UserFacingException("Only \"use the new hold\" takes the boulders that should use it.");
        }

        var chosen = newHoldBoulderIds.ToHashSet();
        var shown = view.Boulders.Select(b => b.Id).ToHashSet();
        if (!chosen.IsSubsetOf(shown))
        {
            throw new UserFacingException(NotShownBoulder);
        }

        if (chosen.Count == 0 && shown.Count > 0)
        {
            return (LookOverAnswer.RemoveHold, null);
        }

        return (answer, chosen.SetEquals(shown) ? null : chosen);
    }

    /// <summary>Runs "use the new hold": the merge for every boulder, or the mix for <paramref name="onlyThese"/>.</summary>
    private async Task UseNewHoldAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, IReadOnlySet<Guid>? onlyThese, CancellationToken ct)
    {
        var clearable = await ClearableAsync(db, finding, view, onlyThese ?? view.Boulders.Select(b => b.Id).ToHashSet(), ct);
        if (onlyThese is null)
        {
            var merge = await duplicates.MergeAsync(finding.WallId, view.OtherHoldId!.Value, view.HoldId!.Value, HoldMergeMode.KeepLeft, ct);
            finding.MergeBatchId = merge.BatchId;

            // On the answer's own context and batch: the merge has committed by now.
            finding.ClearedBoulderIds = await ClearMarksAsync(db, clearable, ct);
            return;
        }

        var (batchId, cleared) = await UseNewHoldForSomeAsync(db, finding, view, onlyThese, clearable, ct);
        (finding.MergeBatchId, finding.ClearedBoulderIds, finding.NewHoldBoulderIds) = (batchId, cleared, [.. onlyThese]);
    }

    /// <summary>
    /// The mix, in one save inside one batch: the yes boulders move as in the merge, their marks come off, and the old
    /// hold is removed through <see cref="HoldDeletion"/> with <see cref="HoldDeleteBoulderPolicy.DetachAndFlagHistoric"/>,
    /// which by then only finds the no boulders on it.
    /// </summary>
    private async Task<(Guid? BatchId, List<Guid> Cleared)> UseNewHoldForSomeAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, IReadOnlySet<Guid> onlyThese, HashSet<Guid> clearable, CancellationToken ct)
    {
        using var wallLock = WallHoldWriteLock.TryAcquire(
            finding.WallId, "Another update of this wall's holds (a clean-up, an outline upgrade, a merge or a wall update) is running. Try again in a moment.");
        var old = await db.Holds.FirstOrDefaultAsync(h => h.Id == view.HoldId && h.WallId == finding.WallId, ct);
        var replacement = await db.Holds.FirstOrDefaultAsync(h => h.Id == view.OtherHoldId && h.WallId == finding.WallId, ct);
        if (old is null || replacement is null)
        {
            throw new UserFacingException("One of these holds no longer exists. Reload the page.");
        }

        var (moved, _) = await HoldDuplicateService.MoveBouldersAsync(db, replacement, old, onlyThese, ct);
        var cleared = ClearTracked(await db.Boulders.Where(b => clearable.Contains(b.Id)).ToListAsync(ct));
        var historic = await HoldDeletion.PrepareHoldForDeleteAsync(db, old.Id, HoldDeleteBoulderPolicy.DetachAndFlagHistoric, ct: ct);
        db.Holds.Remove(old);
        ct.ThrowIfCancellationRequested();
        using var scope = journal?.BeginBatch(HoldDuplicateService.BatchLabel, ChangeJournalScopeKind.Wall, finding.WallId);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Look-over finding {FindingId}: {Moved} boulders moved from hold {Old} onto {New}, {Historic} made historic with it, {Cleared} cleared",
            finding.Id, moved, old.Id, replacement.Id, historic, cleared.Count);
        return ((scope as ChangeJournalBatchScope)?.BatchId, cleared);
    }

    /// <summary>
    /// The yes boulders whose review mark this replacement alone explained and that nothing else keeps marked: no other
    /// marked or changed hold, no other open finding, no merge since, and not on the new hold already (the merge then
    /// marks it for using both). Worked out before the hold operation, while the old hold still has its boulders.
    /// </summary>
    private static async Task<HashSet<Guid>> ClearableAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, IReadOnlySet<Guid> yes, CancellationToken ct)
    {
        var (oldId, newId) = (view.HoldId!.Value, view.OtherHoldId!.Value);
        var clearable = new HashSet<Guid>();
        foreach (var id in yes.Intersect(finding.MarkedBoulderIds ?? []))
        {
            var keep = !await db.Boulders.AnyAsync(b => b.Id == id && b.NeedsReview && !b.IsHistoric && !b.IsArchived && b.Generation == finding.Generation, ct)
                || await db.BoulderHolds.AnyAsync(
                    bh => bh.BoulderId == id && (bh.HoldId == newId || (bh.HoldId != oldId
                        && (bh.Hold.NeedsReview || db.HoldGenerationLinks.Any(l => l.NewHoldId == bh.HoldId && l.Kind == HoldGenerationLinkKind.Changed)))),
                    ct)
                || await HoldMoveRemeasurer.HasOpenFindingAsync(db, finding.WallId, id, finding.Id, ct)
                || await MergedSinceAsync(db, finding, id, ct);
            if (!keep)
            {
                clearable.Add(id);
            }
        }

        return clearable;
    }

    /// <summary>Takes the mark off the clearable boulders that still have it (tracked; the caller saves).</summary>
    private static async Task<List<Guid>> ClearMarksAsync(BlocwerkDbContext db, HashSet<Guid> clearable, CancellationToken ct) =>
        clearable.Count == 0 ? [] : ClearTracked(await db.Boulders.Where(b => clearable.Contains(b.Id)).ToListAsync(ct));

    private static List<Guid> ClearTracked(List<Boulder> boulders)
    {
        var cleared = boulders.Where(b => b is { NeedsReview: true, IsHistoric: false }).ToList();
        cleared.ForEach(b => b.NeedsReview = false);
        return [.. cleared.Select(b => b.Id)];
    }
}
