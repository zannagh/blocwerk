// <copyright file="WallLookOverService.Findings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The list: each finding with the hold as it is NOW (it may have been edited or deleted since the update), the current
/// boulders on it and the answers it takes.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<LookOverFindingView>> ListAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _, wallAdmin) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var completedAt = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.LookOverCompletedAt).FirstOrDefaultAsync(ct);
            var views = await ToViewsAsync(db, await CurrentRoundAsync(db, wallId, completedAt, ct), wallAdmin, ct);
            return views
                .OrderBy(v => LookOverRules.Order.ToList().IndexOf(v.Kind))
                .ThenBy(v => v.Status)
                .ThenByDescending(v => v.Boulders.Count)
                .ThenBy(v => v.Confidence ?? 1)
                .ThenBy(v => v.Id)
                .ToList();
        }
    }

    private static async Task<List<LookOverFindingView>> ToViewsAsync(
        BlocwerkDbContext db, IReadOnlyList<WallLookOverFinding> findings, bool wallAdmin, CancellationToken ct)
    {
        var ids = findings.SelectMany(f => new[] { f.HoldId, f.OtherHoldId, f.OldHoldId }).OfType<Guid>().Distinct().ToList();
        var holds = await db.Holds.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new LookOverHoldNow(h.Id, h.Name, h.Color, h.WallPanelId, h.Generation, h.FacetId != null))
            .ToDictionaryAsync(h => h.Id, ct);
        var boulders = (await db.BoulderHolds.AsNoTracking()
                .Where(bh => ids.Contains(bh.HoldId) && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
                .Select(bh => new { bh.HoldId, bh.BoulderId, bh.Boulder.Name, bh.Boulder.NeedsReview })
                .ToListAsync(ct))
            .ToLookup(b => b.HoldId, b => new LookOverBoulder(b.BoulderId, b.Name, b.NeedsReview));
        return findings.Select(f => ToView(f, holds, f.HoldId is { } id ? boulders[id] : [], wallAdmin)).ToList();
    }

    private static LookOverFindingView ToView(
        WallLookOverFinding f, Dictionary<Guid, LookOverHoldNow> holds, IEnumerable<LookOverBoulder> boulders, bool wallAdmin)
    {
        var hold = f.HoldId is { } id ? holds.GetValueOrDefault(id) : null;
        var other = f.OtherHoldId is { } otherId ? holds.GetValueOrDefault(otherId) : null;
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
            [.. boulders.OrderBy(b => b.Name)],
            open ? LookOverRules.AnswersFor(f.Kind, hold is not null, mergeable && wallAdmin) : [],
            open && mergeable && !wallAdmin);
    }
}
