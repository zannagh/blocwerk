// <copyright file="WallBigUpdateService.Panels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The per-panel view of an in-flight update that the review surfaces draw. Hold coordinates are
/// PANEL-normalized, so a pane that shows one panel's photo may only overlay that panel's holds; this
/// partial is what pairs the two, so the UI never has to re-derive a (wall-wide, and therefore wrong)
/// old-hold set of its own.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Projects a carried old hold into the normalized shape the review overlays draw, so the session can
    /// hand the UI the exact set the matcher used instead of the UI re-deriving a wall-wide one.
    /// </summary>
    private static PanelHold ToPanelHold(Hold hold) =>
        new(hold.Id, hold.X, hold.Y, hold.Radius, hold.Color, hold.Category, hold.ShapePoints, hold.IsVirtual, hold.ShapeHoles);

    /// <summary>
    /// Pairs every re-photographed panel with its live ("before") panel and the old holds this update will
    /// carry on it. Hold coordinates are PANEL-normalized, so this is what lets a review pane draw an old
    /// set over the right photo instead of re-projecting foreign-panel holds onto the centre image. The
    /// live panel at a position is the highest-generation one that still has a committed photo, which is
    /// the panel row the <c>/panels/{id}/photo</c> route serves.
    /// </summary>
    private static async Task<List<CarriedPanelOldHolds>> BuildCarriedPanelsAsync(
        BlocwerkDbContext db,
        Guid wallId,
        int stagedGen,
        IReadOnlyDictionary<(int Col, int Row), List<Hold>> oldByPosition,
        IReadOnlySet<Guid> unalignedPanelIds)
    {
        var staged = await db.WallPanels
            .Where(p => p.WallId == wallId && p.Generation == stagedGen && p.StagedPhoto != null)
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync();

        var liveByPosition = (await db.WallPanels
                .Where(p => p.WallId == wallId && p.Photo != null)
                .Select(p => new { p.Id, p.Col, p.Row, p.Generation })
                .ToListAsync())
            .GroupBy(p => (p.Col, p.Row))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Generation).First().Id);

        var panels = new List<CarriedPanelOldHolds>();
        foreach (var panel in staged.OrderBy(p => p.Row).ThenBy(p => p.Col))
        {
            var position = (panel.Col, panel.Row);
            var holds = oldByPosition.GetValueOrDefault(position) ?? [];
            Guid? livePanelId = liveByPosition.TryGetValue(position, out var liveId) ? liveId : null;
            panels.Add(new CarriedPanelOldHolds(
                panel.Col,
                panel.Row,
                livePanelId,
                panel.Id,
                holds.Select(ToPanelHold).ToList(),
                unalignedPanelIds.Contains(panel.Id)));
        }

        return panels;
    }
}
