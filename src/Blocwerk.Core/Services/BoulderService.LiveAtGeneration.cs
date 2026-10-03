// <copyright file="BoulderService.LiveAtGeneration.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which of a boulder's hold rows from BEFORE a viewed generation were still live at it, for the "Then" view.
/// </summary>
public partial class BoulderService
{
    /// <summary>
    /// Of <paramref name="holdIds"/> (rows older than <paramref name="generation"/>), those still live at it:
    /// a row on a panel is live until its panel position is re-shot, i.e. while no newer panel row at the same
    /// (Col, Row) with a photo exists at or before the generation. That covers carried holds and removed ones
    /// alike, since every update writes new panel rows. A row without a panel is the legacy centre photo, which
    /// every update replaces, so it is never live at a later generation.
    /// <para>
    /// The lower bound is the row's own <c>Generation</c> (the caller only asks for rows at or below the target).
    /// Holds record no creation time or creation generation of their own, and a hold added by hand to a panel a
    /// subset update skipped is stamped with that panel's older generation, so such a hold can show in a "Then"
    /// view of a generation before it was really added. Nothing recorded tells the two apart, so that case is
    /// left as is.
    /// </para>
    /// </summary>
    private static async Task<HashSet<Guid>> LoadLiveAtGenerationAsync(
        BlocwerkDbContext db,
        Guid wallId,
        IReadOnlyCollection<Guid> holdIds,
        int generation,
        CancellationToken ct)
    {
        if (holdIds.Count == 0)
        {
            return [];
        }

        var ids = holdIds.ToList();
        var holds = await db.Holds
            .AsNoTracking()
            .Where(h => h.WallId == wallId && ids.Contains(h.Id) && h.WallPanelId != null)
            .Select(h => new { h.Id, PanelId = h.WallPanelId!.Value })
            .ToListAsync(ct);
        var panels = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null)
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation })
            .ToListAsync(ct);
        var byId = panels.ToDictionary(p => p.Id);

        var live = new HashSet<Guid>();
        foreach (var hold in holds)
        {
            if (!byId.TryGetValue(hold.PanelId, out var own))
            {
                continue;
            }

            var reShot = panels.Any(p => p.Col == own.Col && p.Row == own.Row
                && p.Generation > own.Generation && p.Generation <= generation);
            if (!reShot)
            {
                live.Add(hold.Id);
            }
        }

        return live;
    }
}
