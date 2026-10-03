// <copyright file="HoldLinkSuggestionService.Views.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.HoldLinks;

/// <summary>Reading the pending suggestions for the review list.</summary>
public sealed partial class HoldLinkSuggestionService
{
    private static async Task<List<HoldLinkSuggestionView>> ViewsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var rows = await db.HoldLinkSuggestions.AsNoTracking()
            .Where(s => s.WallId == wallId && s.Status == HoldLinkSuggestionStatus.Pending)
            .ToListAsync(ct);
        var ids = rows.SelectMany(r => new[] { r.HoldAId, r.HoldBId }).Distinct().ToList();
        var holds = await db.Holds.AsNoTracking()
            .Where(h => h.WallId == wallId && ids.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, ct);
        var panelIds = holds.Values.Select(h => h.WallPanelId).OfType<Guid>().Distinct().ToList();
        var names = await db.WallPanels.AsNoTracking()
            .Where(p => panelIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToDictionaryAsync(p => p.Id, p => PanelPositionName.Describe(p.Col, p.Row), ct);
        return rows
            .Where(r => holds.TryGetValue(r.HoldAId, out var a) && a.WallPanelId is not null
                && holds.TryGetValue(r.HoldBId, out var b) && b.WallPanelId is not null)
            .OrderBy(r => r.DistanceMm)
            .Select(r => new HoldLinkSuggestionView(Side(holds[r.HoldAId], names), Side(holds[r.HoldBId], names), r.DistanceMm))
            .ToList();
    }

    private static HoldLinkSuggestionSide Side(Hold h, IReadOnlyDictionary<Guid, string> names) =>
        new(h.Id, h.WallPanelId!.Value, names.GetValueOrDefault(h.WallPanelId!.Value, "Panel"), h.X, h.Y, h.Radius, h.Color);
}
