// <copyright file="WallBigUpdateService.CarryScope.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which old holds an update carries, and which old photos it needs to do so. Both are defined by the
/// LIVE panel at each re-photographed position (<see cref="LiveWallHolds"/>), never by the wall's current
/// generation: a subset promote leaves the panels it did not re-shoot, and their holds, at an older
/// generation, and those holds are still the live wall. Keying the carry on
/// <c>Generation == CurrentGeneration</c> made the next update that re-shot such a panel skip every one
/// of its holds, so they dropped out of the live wall with their boulder memberships and links.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// The ids of the live panels at the positions this update re-photographs: the "before" side of each
    /// panel's carryover, whatever generation it is at.
    /// </summary>
    internal static async Task<List<Guid>> LoadLiveUpdatedPanelIdsAsync(
        BlocwerkDbContext db,
        Guid wallId,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        IReadOnlySet<(int Col, int Row)> updatedPositions)
    {
        var live = await LiveWallHolds.LoadPanelIdsAsync(db, wallId);
        return live
            .Where(id => panelPositions.TryGetValue(id, out var pos) && updatedPositions.Contains(pos))
            .ToList();
    }

    /// <summary>
    /// The old holds this update carries: every hold on a live panel at a re-photographed position (at any
    /// generation up to the wall's), plus the legacy centre-photo holds without a panel when the centre is
    /// re-shot. Tracked, because the promote advances these rows' memberships in place.
    /// </summary>
    private static async Task<List<Hold>> LoadCarriedOldHoldsAsync(
        BlocwerkDbContext db,
        Wall wall,
        IReadOnlyCollection<Guid> liveUpdatedPanelIds,
        IReadOnlySet<(int Col, int Row)> updatedPositions)
    {
        var panelIds = liveUpdatedPanelIds.ToList();
        var generation = wall.CurrentGeneration;
        var centreUpdated = updatedPositions.Contains((0, 0));
        return await db.Holds
            .Where(h => h.WallId == wall.Id
                && ((h.WallPanelId != null && panelIds.Contains(h.WallPanelId.Value) && h.Generation <= generation)
                    || (centreUpdated && h.WallPanelId == null && h.Generation == generation)))
            .ToListAsync();
    }

    /// <summary>
    /// The raw pixel size of each staged panel photo by panel id (panels whose photo does not decode are
    /// left out). Only the header is decoded.
    /// </summary>
    private static async Task<Dictionary<Guid, (int Width, int Height)>> LoadStagedPhotoSizesAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> panelIds)
    {
        var ids = panelIds.ToList();
        var photos = await db.WallPanels
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.StagedPhoto != null)
            .Select(p => new { p.Id, p.StagedPhoto })
            .ToListAsync();
        var sizes = new Dictionary<Guid, (int Width, int Height)>();
        foreach (var photo in photos)
        {
            if (OverlapSeedLoader.RawSize(photo.StagedPhoto!) is { } size)
            {
                sizes[photo.Id] = size;
            }
        }

        return sizes;
    }

    /// <summary>
    /// The committed photos of the given panels by id. Only the live panels at re-photographed positions
    /// are ever asked for: superseded rows keep their photo as history, so reading every panel photo of
    /// the wall grew with each update (panels x generations of multi-megabyte images) on every resume.
    /// </summary>
    private static async Task<Dictionary<Guid, byte[]>> LoadPanelPhotosAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> panelIds)
    {
        var ids = panelIds.ToList();
        return (await db.WallPanels
                .Where(p => ids.Contains(p.Id) && p.Photo != null)
                .Select(p => new { p.Id, p.Photo })
                .ToListAsync())
            .ToDictionary(p => p.Id, p => p.Photo!);
    }
}
