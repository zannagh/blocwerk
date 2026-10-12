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
            var since = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.LookOverPendingSince).FirstOrDefaultAsync(ct);
            var views = await ToViewsAsync(db, wallId, await CurrentRound(db, wallId, since).ToListAsync(ct), access, ct);
            return views
                .OrderBy(v => LookOverRules.Rank(v.Kind))
                .ThenBy(v => v.Status)
                .ThenByDescending(v => v.Boulders.Count)
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
            .Select(h => new LookOverHoldNow(h.Id, h.Name, h.Color, h.WallPanelId, h.Generation, h.FacetId != null, false))
            .ToListAsync(ct);
        var holds = rows.ToDictionary(h => h.Id, h => h with { Live = live.Contains((h.PanelId, h.Generation)) });
        var boulders = (await db.BoulderHolds.AsNoTracking()
                .Where(bh => ids.Contains(bh.HoldId) && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
                .Select(bh => new { bh.HoldId, bh.BoulderId, bh.Boulder.Name, bh.Boulder.NeedsReview })
                .ToListAsync(ct))
            .ToLookup(b => b.HoldId, b => new LookOverBoulder(b.BoulderId, b.Name, b.NeedsReview));
        var answerers = findings.Select(f => f.AnsweredByUserId).OfType<Guid>().Distinct().ToList();
        var names = answerers.Count == 0
            ? []
            : await db.Users.IgnoreQueryFilters().Where(u => answerers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return findings
            .Select(f => ToView(f, holds, boulders, access) with
            {
                AnsweredBy = f.AnsweredByUserId is { } by ? names.GetValueOrDefault(by) : null,
                AnswerBoulders = f.AnswerBoulders,
                ClearedBoulders = f.ClearedBoulderIds?.Count ?? 0,
                CanUndo = f is { Status: LookOverFindingStatus.Settled, Answer: LookOverAnswer.Settle },
            })
            .ToList();
    }

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
            old is not null,
            f.FacetId is not null || hold is { Placed: true },
            f.PhotoScore,
            f.Confidence,
            f.Converted,
            hold is null ? [] : [.. boulders[hold.Id].OrderBy(b => b.Name).ThenBy(b => b.Id)],
            open ? LookOverRules.AnswersFor(f.Kind, hold is not null, access.CanEdit, mergeable && access.CanMerge) : [],
            open && mergeable && access.CanEdit && !access.CanMerge);
    }

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
