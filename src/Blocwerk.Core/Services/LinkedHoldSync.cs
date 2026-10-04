// <copyright file="LinkedHoldSync.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Applies <see cref="LinkedHoldSyncRules"/> to a wall's linked holds. It only changes tracked entities and never saves:
/// the caller saves, inside the journal batch it owns, so the sync and the edit that triggered it revert together.
/// Works on the wall's current generation only (links of an older generation describe holds that were replaced).
/// </summary>
public static class LinkedHoldSync
{
    /// <summary>Bump to make the startup pass run again for every wall.</summary>
    public const int Version = 1;

    /// <summary>
    /// The grid cell that wins explicit conflicts: the wall's setting when that cell still exists, else the centre cell
    /// (col 0, row 0), else the cell closest to the centre.
    /// </summary>
    /// <param name="configured">The wall's setting.</param>
    /// <param name="cells">The wall's panel cells as (col, row).</param>
    /// <returns>The winner cell, or null when the wall has no panels.</returns>
    public static (int Col, int Row)? ResolveWinnerCell((int Col, int Row)? configured, IEnumerable<(int Col, int Row)> cells)
    {
        var all = cells.Distinct().ToList();
        if (configured is { } c && all.Contains(c))
        {
            return c;
        }

        return all
            .OrderBy(p => Math.Abs(p.Col) + Math.Abs(p.Row))
            .ThenBy(p => p.Row)
            .ThenBy(p => p.Col)
            .Select(p => ((int Col, int Row)?)p)
            .FirstOrDefault();
    }

    /// <summary>Reconciles every link group of the wall's current generation, or only the group of one hold.</summary>
    /// <param name="db">The context; changes are left unsaved.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="onlyGroupOf">When set, only the link group containing this hold.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What changed.</returns>
    public static async Task<HoldSyncReport> ReconcileAsync(
        BlocwerkDbContext db, Guid wallId, Guid? onlyGroupOf = null, CancellationToken ct = default)
    {
        var report = new HoldSyncReport();
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wallId, ct);
        if (wall is null)
        {
            return report;
        }

        var links = (await db.HoldLinks.AsNoTracking().Where(l => l.WallId == wallId)
                .Select(l => new { l.HoldAId, l.HoldBId }).ToListAsync(ct))
            .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
            .ToList();
        var linkedIds = links.SelectMany(l => new[] { l.HoldAId, l.HoldBId }).Distinct().ToList();
        var groups = HoldPropertySync.ConnectedComponents(linkedIds, links)
            .Where(g => g.Count >= 2 && (onlyGroupOf is not { } only || g.Contains(only)))
            .ToList();
        if (groups.Count == 0)
        {
            return report;
        }

        var wanted = groups.SelectMany(g => g).ToList();
        var holds = await db.Holds
            .Where(h => h.WallId == wallId && h.Generation == wall.CurrentGeneration && wanted.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, ct);

        var position = (await db.WallPanels.AsNoTracking().Where(p => p.WallId == wallId)
                .Select(p => new { p.Id, p.Col, p.Row }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => (p.Col, p.Row));
        var winner = ResolveWinnerCell(wall.WinnerCell(), position.Values);

        foreach (var group in groups)
        {
            var members = group.Where(holds.ContainsKey).Select(id => holds[id]);
            LinkedHoldSyncRules.Apply(Order(members, winner, position), report);
        }

        return report;
    }

    private static List<Hold> Order(
        IEnumerable<Hold> group, (int Col, int Row)? winner, IReadOnlyDictionary<Guid, (int Col, int Row)> position)
    {
        HoldCentrality Rank(Hold h) => h.WallPanelId is { } p && position.TryGetValue(p, out var pos)
            ? new HoldCentrality(h.Id, pos.Col, pos.Row)
            : new HoldCentrality(h.Id, null, null);

        var byId = group.ToDictionary(h => h.Id);
        return HoldPropertySync.ByCentrality(byId.Values.Select(Rank))
            .Select(c => byId[c.HoldId])
            .OrderBy(h => winner is { } w && h.WallPanelId is { } p && position.TryGetValue(p, out var pos) && pos == w ? 0 : 1)
            .ToList();
    }
}
