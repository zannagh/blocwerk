// <copyright file="WallLookOverService.NewHold.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Should this boulder use the new hold in that place?", asked per boulder of a hold another hold replaced. Every
/// boulder yes does what the duplicate review's merge does (old hold gone, every boulder on the new hold); every boulder no is "the old hold is gone"; a mix moves the yes
/// boulders' memberships onto the new hold and removes the old hold, which takes the no boulders with it into history
/// (the old hold is physically gone). Both are one journal batch under the merge's label, so the duplicate review
/// undoes either as one, exactly. A yes boulder whose review mark this replacement alone explained
/// (<see cref="WallLookOverFinding.MarkedBoulderIds"/>) loses the mark: a person just answered for it.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>The refusal when the per-boulder answer names a boulder the card did not show.</summary>
    public const string NotShownBoulder = "Only the boulders that use this hold can be moved onto the new hold. Reload the page and check again.";

    /// <summary>
    /// Under the wall lock, the current boulders on the old hold must still be the ones the user answered for (one that
    /// joined since would otherwise be retired unasked). Returns the memberships to move: null for all, else the yes
    /// boulders plus every archived or historic one, whose frozen record keeps its hold on the new hold.
    /// </summary>
    internal static async Task<IReadOnlySet<Guid>?> MovingUnderLockAsync(
        BlocwerkDbContext db, Guid oldId, LookOverFindingView view, IReadOnlySet<Guid>? onlyThese, CancellationToken ct)
    {
        var uses = await db.BoulderHolds.AsNoTracking()
            .Where(bh => bh.HoldId == oldId)
            .Select(bh => new { bh.BoulderId, Frozen = bh.Boulder.IsArchived || bh.Boulder.IsHistoric })
            .ToListAsync(ct);
        if (!uses.Where(u => !u.Frozen).Select(u => u.BoulderId).ToHashSet().SetEquals(view.Boulders.Select(b => b.Id)))
        {
            throw new UserFacingException(BouldersChanged);
        }

        return onlyThese is null ? null : onlyThese.Concat(uses.Where(u => u.Frozen).Select(u => u.BoulderId)).ToHashSet();
    }

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

    /// <summary>
    /// "Use the new hold", for every boulder or for <paramref name="onlyThese"/>, in one save inside one batch under the
    /// merge's label: the yes boulders move as in the merge (a boulder on both holds keeps the stronger role and is
    /// marked), their marks come off, the new hold takes the old one's flags and what is stacked on it, and the old
    /// hold is removed through <see cref="HoldDeletion"/> with <see cref="HoldDeleteBoulderPolicy.DetachAndFlagHistoric"/>,
    /// which by then only finds the no boulders on it. Undone as one in the duplicate review, marks included.
    /// </summary>
    private async Task UseNewHoldAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, IReadOnlySet<Guid>? onlyThese, CancellationToken ct)
    {
        var journalled = journal ?? throw new InvalidOperationException("\"Use the new hold\" needs the change journal, so the answer can be undone.");
        using var wallLock = WallHoldWriteLock.TryAcquire(
            finding.WallId, "Another update of this wall's holds (a clean-up, an outline upgrade, a merge or a wall update) is running. Try again in a moment.");
        var (old, replacement) = await PairAsync(db, finding, view, ct);
        var toMove = await MovingUnderLockAsync(db, old.Id, view, onlyThese, ct);
        var clearable = await ClearableAsync(db, finding, view, onlyThese ?? view.Boulders.Select(b => b.Id).ToHashSet(), ct);
        var (moved, _) = await HoldDuplicateService.MoveBouldersAsync(db, replacement, old, toMove, ct);
        var cleared = ClearTracked(await db.Boulders.Where(b => clearable.Contains(b.Id)).ToListAsync(ct));
        HoldMergeRules.MergeFlags(replacement, old);
        await HoldStacker.MergeAsync(db, replacement, old, ct);
        var historic = await HoldDeletion.PrepareHoldForDeleteAsync(db, old.Id, HoldDeleteBoulderPolicy.DetachAndFlagHistoric, ct: ct);
        db.Holds.Remove(old);
        ct.ThrowIfCancellationRequested();
        using (var scope = journalled.BeginBatch(HoldDuplicateService.BatchLabel, ChangeJournalScopeKind.Wall, finding.WallId))
        {
            await db.SaveChangesAsync(ct);
            if ((await LinkedHoldSync.ReconcileAsync(db, finding.WallId, replacement.Id, ct)).Any)
            {
                await db.SaveChangesAsync(ct);
            }

            finding.MergeBatchId = (scope as ChangeJournalBatchScope)?.BatchId
                ?? throw new InvalidOperationException("The change journal opened no batch for \"use the new hold\".");
        }

        finding.ClearedBoulderIds = cleared;
        logger.LogInformation(
            "Look-over finding {FindingId}: {Moved} boulders moved from hold {Old} onto {New}, {Historic} made historic with it, {Cleared} cleared, batch {BatchId}",
            finding.Id, moved, old.Id, replacement.Id, historic, cleared.Count, finding.MergeBatchId);
    }

    /// <summary>The old hold and the new one on its spot, as the merge checks them: both still there, on one photo.</summary>
    private static async Task<(Hold Old, Hold Replacement)> PairAsync(BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, CancellationToken ct)
    {
        var old = await db.Holds.FirstOrDefaultAsync(h => h.Id == view.HoldId && h.WallId == finding.WallId, ct);
        var replacement = await db.Holds.FirstOrDefaultAsync(h => h.Id == view.OtherHoldId && h.WallId == finding.WallId, ct);
        if (old is null || replacement is null)
        {
            throw new UserFacingException("One of these holds no longer exists. Reload the page.");
        }

        if (old.Id == replacement.Id)
        {
            throw new UserFacingException("The old and the new hold are the same hold. Reload the page.");
        }

        if (old.WallPanelId != replacement.WallPanelId || old.Generation != replacement.Generation)
        {
            throw new UserFacingException("These holds are on different photos, so they cannot be merged.");
        }

        return (old, replacement);
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

    private static List<Guid> ClearTracked(List<Boulder> boulders)
    {
        var cleared = boulders.Where(b => b is { NeedsReview: true, IsHistoric: false }).ToList();
        cleared.ForEach(b => b.NeedsReview = false);
        return [.. cleared.Select(b => b.Id)];
    }
}
