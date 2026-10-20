// <copyright file="WallLookOverService.Findings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The list: each finding with the hold as it is NOW (it may have been edited or deleted since the update, or its panel
/// photographed again), the current boulders on it and the answers it takes from this user.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<LookOverFindingView>> ListAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            await LookOverReconciler.ReconcileAsync(db, wallId, ct);
            await DiscardedDetections.RefreshOverlapAsync(db, wallId, ct);
            var since = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.LookOverPendingSince).FirstOrDefaultAsync(ct);
            var round = await CurrentRound(db, wallId, since).ToListAsync(ct);
            var views = await WithPicturesAsync(db, wallId, round, await ToViewsAsync(db, wallId, round, access, ct), ct);
            return views
                .OrderBy(v => LookOverRules.Rank(v.Kind))
                .ThenBy(v => v.Status)
                .ThenByDescending(v => v.Boulders.Count)
                .ThenBy(v => v.DiscardedOrder)
                .ThenBy(v => v.Confidence ?? 1)
                .ThenBy(v => v.Id)
                .ToList();
        }
    }

    private static async Task<List<LookOverFindingView>> ToViewsAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyList<WallLookOverFinding> findings, LookOverAccess access, CancellationToken ct)
    {
        var ids = findings.SelectMany(f => new[] { f.HoldId, f.OtherHoldId, f.OldHoldId }).OfType<Guid>().Distinct().ToList();
        var live = await LivePhotosAsync(db, wallId, ct);
        var rows = await db.Holds.AsNoTracking()
            .Where(h => h.WallId == wallId && ids.Contains(h.Id))
            .Select(h => new LookOverHoldNow(h.Id, h.Name, h.Color, h.WallPanelId, h.Generation, h.FacetId != null, false, h.KeptUnconfirmedGeneration != null))
            .ToListAsync(ct);
        var holds = rows.ToDictionary(h => h.Id, h => h with { Live = live.Contains((h.PanelId, h.Generation)) });
        var boulders = (await db.BoulderHolds.AsNoTracking()
                .Where(bh => ids.Contains(bh.HoldId) && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
                .Select(bh => new { bh.HoldId, bh.BoulderId, bh.Boulder.Name, bh.Boulder.NeedsReview })
                .ToListAsync(ct))
            .ToLookup(b => b.HoldId, b => new LookOverBoulder(b.BoulderId, b.Name, b.NeedsReview));
        var since = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.LookOverPendingSince).FirstOrDefaultAsync(ct);
        var answerers = findings.Select(f => f.AnsweredByUserId).OfType<Guid>().Distinct().ToList();
        var names = answerers.Count == 0
            ? []
            : await db.Users.IgnoreQueryFilters().Where(u => answerers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var retired = await RetiredByNewHoldAsync(db, findings, ct);
        return findings
            .Select(f => (Finding: f, Kept: f.HoldId is { } id && holds.GetValueOrDefault(id) is { Live: true, Kept: true }))
            .Select(x => (x.Finding, x.Kept, View: ToView(x.Finding, holds, boulders, access)))
            .Select(x => Detailed(x.View, x.Finding, x.Kept, access) with
            {
                AnsweredBy = x.Finding.AnsweredByUserId is { } by ? names.GetValueOrDefault(by) : null,
                CanUndo = x.Finding.Kind == LookOverFindingKind.DiscardedDetection
                    ? CanUndoDiscarded(x.Finding, holds, boulders)
                    : CanUndo(x.Finding, holds, since),
                NewHoldBoulders = x.Finding.MergeBatchId is { } batch && retired.GetValueOrDefault(batch) is > 0 and var gone
                    ? (x.Finding.AnswerBoulders ?? 0) - gone
                    : null,
            })
            .ToList();
    }

    /// <summary>
    /// Per "use the new hold" batch, how many boulders it made historic (the ones answered no): read from the batch
    /// itself, which records a boulder's retirement as a change of its historic flag. Zero for a plain merge and for a
    /// batch the duplicate review has reverted.
    /// </summary>
    private static async Task<Dictionary<Guid, int>> RetiredByNewHoldAsync(BlocwerkDbContext db, IReadOnlyList<WallLookOverFinding> findings, CancellationToken ct)
    {
        var batches = findings.Where(f => f.Answer == LookOverAnswer.UseNewHold).Select(f => f.MergeBatchId).OfType<Guid>().ToList();
        if (batches.Count == 0)
        {
            return [];
        }

        return (await db.ChangeJournalEntries.AsNoTracking()
                .Where(e => batches.Contains(e.BatchId) && e.EntityType == nameof(Boulder) && e.Op == ChangeJournalOp.Update
                    && db.ChangeJournalBatches.Any(b => b.Id == e.BatchId && b.Status == ChangeJournalStatus.Recorded)
                    && e.AfterJson != null && e.AfterJson.Contains(nameof(Boulder.IsHistoric)))
                .Select(e => e.BatchId)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static LookOverFindingView Detailed(LookOverFindingView view, WallLookOverFinding f, bool kept, LookOverAccess access) =>
        view with
        {
            AnswerBoulders = f.AnswerBoulders,
            ClearedBoulders = f.ClearedBoulderIds?.Count ?? 0,
            HoldKept = kept,
            KeptAtUpdate = f.HoldKept,
            CanReassign = kept && f.Status == LookOverFindingStatus.Open && access.CanMerge,
            Covered = f.Covered,
            ClosedReason = f.ClosedReason,
            PanelId = f.PanelId,
            SpotX = f.X,
            SpotY = f.Y,
            DiscardReason = f.DiscardReason,
            Overlap = f.Overlap ?? 0,
        };

    private static LookOverFindingView ToView(
        WallLookOverFinding f, Dictionary<Guid, LookOverHoldNow> holds, ILookup<Guid, LookOverBoulder> boulders, LookOverAccess access)
    {
        // Only a LIVE hold is "the hold": a retained row of an earlier generation is history and is never changed from here.
        var hold = f.HoldId is { } id && holds.GetValueOrDefault(id) is { Live: true } liveHold ? liveHold : null;
        var other = f.OtherHoldId is { } otherId && holds.GetValueOrDefault(otherId) is { Live: true } liveOther ? liveOther : null;
        var old = f.OldHoldId is { } oldId ? holds.GetValueOrDefault(oldId) : null;

        // The merge works on two holds of one photo; anything else cannot be merged and is not offered.
        var mergeable = hold is not null && other is not null && hold.PanelId == other.PanelId && hold.Generation == other.Generation
            && LookOverRules.CanHaveOther(f.Kind);
        var open = f.Status == LookOverFindingStatus.Open;
        return new LookOverFindingView(
            f.Id,
            f.Kind,
            f.Status,
            f.Answer,
            f.Generation,
            f.OldHoldId,
            hold?.Id,
            other?.Id,
            hold?.Name ?? hold?.Color ?? old?.Name ?? old?.Color,
            old is not null || (f.Kind == LookOverFindingKind.UnsureLink && other is not null),
            (f.FacetId is not null || hold is { Placed: true }) && !LookOverCrops.KnownToHaveNoModelPicture(f.Id),
            f.PhotoScore,
            f.Confidence,
            f.Converted,
            hold is null ? [] : [.. boulders[hold.Id].OrderBy(b => b.Name).ThenBy(b => b.Id)],
            open ? LookOverRules.AnswersFor(f.Kind, hold is not null, access.CanEdit, mergeable && access.CanMerge) : [],
            open && mergeable && access.CanEdit && !access.CanMerge);
    }

    /// <summary>
    /// An answer can be taken back while that still means something: it changed nothing on the wall, it was given in
    /// the round that is still running (not before the last Finish), and the hold it was about is still a live hold
    /// (not removed, and its panel not photographed again since).
    /// </summary>
    private static bool CanUndo(WallLookOverFinding f, Dictionary<Guid, LookOverHoldNow> holds, DateTimeOffset? pendingSince) =>
        f is { Status: LookOverFindingStatus.Settled, Answer: LookOverAnswer.Settle }
        && pendingSince is { } since && f.AnsweredAt >= since
        && (f.HoldId is not { } id || holds.GetValueOrDefault(id) is { Live: true });

    /// <summary>
    /// The wall's live photos as (panel, generation): per grid position the newest committed panel (the rule the live
    /// viewers use), plus the wall's own photo at its current generation for holds without a panel.
    /// </summary>
    private static async Task<HashSet<(Guid? PanelId, int Generation)>> LivePhotosAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var panels = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null)
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation })
            .ToListAsync(ct);
        var live = panels
            .GroupBy(p => (p.Col, p.Row))
            .Select(g => g.OrderByDescending(p => p.Generation).ThenBy(p => p.Id).First())
            .Select(p => ((Guid?)p.Id, p.Generation))
            .ToHashSet();
        var current = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.CurrentGeneration).FirstOrDefaultAsync(ct);
        live.Add((null, current));
        return live;
    }
}
