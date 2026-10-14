// <copyright file="WallBigUpdateService.OldCentrePhoto.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The "before" photo of grid position (0,0). Old centre holds are normalized to the live centre PANEL photo,
/// and a panel crop rewrites that photo (and re-maps its holds and markers) without touching
/// <see cref="Wall.Photo"/>, which only follows at the next promote. So anything that reads old centre holds
/// off an image must use the panel photo; <see cref="Wall.Photo"/> is only the fallback for a wall that has
/// no live centre panel.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>The live centre panel among the loaded old panel photos, or null when the wall has none.</summary>
    internal static (Guid PanelId, byte[] Photo)? LiveCentrePanelPhoto(
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions)
    {
        foreach (var (panelId, photo) in oldPanelPhotosById)
        {
            if (panelPositions.TryGetValue(panelId, out var pos) && pos == (0, 0))
            {
                return (panelId, photo);
            }
        }

        return null;
    }

    /// <summary>The photo the old centre holds are normalized to: the live centre panel's, else <see cref="Wall.Photo"/>.</summary>
    internal static byte[]? OldCentrePhoto(
        Wall wall,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions) =>
        LiveCentrePanelPhoto(oldPanelPhotosById, panelPositions)?.Photo ?? wall.Photo;

    /// <summary>As <see cref="OldCentrePhoto"/>, for a caller that has not loaded the old panel photos.</summary>
    private static async Task<byte[]?> LoadOldCentrePhotoAsync(BlocwerkDbContext db, Wall wall)
    {
        var positions = await LoadPanelPositionsAsync(db, wall.Id);
        var liveCentre = (await LiveWallHolds.LoadPanelIdsAsync(db, wall.Id))
            .Where(id => positions.TryGetValue(id, out var pos) && pos == (0, 0))
            .ToList();
        return OldCentrePhoto(wall, await LoadPanelPhotosAsync(db, liveCentre), positions);
    }

    /// <summary>
    /// The photo a promote archives for the generation it retires: the live centre panel's, with its content type,
    /// because a crop may have cut it after the last promote and the retired holds are positioned on the cut photo.
    /// <see cref="Wall.Photo"/> is the fallback for a wall without a live centre panel.
    /// </summary>
    private static async Task<(byte[] Photo, string? ContentType)?> LoadRetiringCentrePhotoAsync(BlocwerkDbContext db, Wall wall)
    {
        var live = await LiveWallHolds.LoadPanelIdsAsync(db, wall.Id);
        var centre = await db.WallPanels.AsNoTracking()
            .Where(p => live.Contains(p.Id) && p.Col == 0 && p.Row == 0)
            .Select(p => new { p.Photo, p.PhotoContentType })
            .FirstOrDefaultAsync();
        if (centre?.Photo is { } photo)
        {
            return (photo, centre.PhotoContentType);
        }

        return wall.Photo is { } legacy ? (legacy, wall.PhotoContentType) : null;
    }
}
