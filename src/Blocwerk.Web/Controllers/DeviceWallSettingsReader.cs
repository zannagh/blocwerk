// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Reads a wall's settings for the companion app without loading its photo bytes: the row's scalar fields through the
/// wall membership filter, the card photo's sha (<see cref="IAssetHashReader"/>) and the live panel count
/// (<see cref="IWallPanelService"/>), as the web settings card shows them.
/// </summary>
public sealed class DeviceWallSettingsReader(
    IDbContextFactory<BlocwerkDbContext> contexts,
    IAssetHashReader hashes,
    IWallPanelService panels)
{
    /// <summary>The settings of <paramref name="wallId"/> as <paramref name="caller"/> sees them, or null when it is gone.</summary>
    public async Task<DeviceWallSettingsResponse?> ReadAsync(Guid wallId, WallCaller caller, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = caller.UserId;
        var wall = await db.Walls.AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new
            {
                w.Name,
                w.Description,
                w.Angle,
                w.UnderMaintenance,
                w.OwnerId,
                w.AllowAnonymousKioskSetting,
                w.AllowKioskKeyboardShortcuts,
            })
            .FirstOrDefaultAsync(ct);
        if (wall is null)
        {
            return null;
        }

        var photos = await hashes.WallPhotosAsync([wallId], ct);
        var livePanels = (await panels.GetPanelsAsync(wallId)).Count(p => p.IsLive);
        return new DeviceWallSettingsResponse(
            wall.Name,
            wall.Description,
            wall.Angle,
            wall.UnderMaintenance,
            caller.RoleName,
            wall.OwnerId,
            photos.GetValueOrDefault(wallId),
            livePanels,
            wall.AllowAnonymousKioskSetting,
            wall.AllowKioskKeyboardShortcuts);
    }
}
