// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The "iPhone scan" wall setting (<see cref="IWallUpdateModeService"/>).</summary>
public sealed class WallUpdateModeService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ILogger<WallUpdateModeService> logger,
    IKioskContext? kioskContext = null) : IWallUpdateModeService
{
    /// <summary>The refusal once a scan has been applied.</summary>
    public const string LockedMessage =
        "This wall has been moved onto an iPhone scan. To go back to panel photos, start a panel update instead.";

    private const string Action = "Changing how this wall is updated";

    public async Task<WallUpdateModeView> GetAsync(Guid wallId)
    {
        await using var db = await OpenForAdminAsync(wallId);
        var wall = await LoadAsync(db, wallId);
        return View(wall);
    }

    public async Task<WallUpdateModeView> SetAsync(Guid wallId, WallUpdateMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await using var db = await OpenForAdminAsync(wallId);
        var wall = await LoadAsync(db, wallId);

        // The seam for the first scan's migration: once a scan is applied, the wall is no panel wall any more.
        if (mode == WallUpdateMode.Panels && wall.ActiveScanCaptureId is not null)
        {
            throw new UserFacingException(LockedMessage);
        }

        if (wall.UpdateMode != mode)
        {
            wall.UpdateMode = mode;
            await db.SaveChangesAsync();
            logger.LogInformation("Wall {WallId} update mode set to {Mode} by {UserId}", wallId, mode, db.CurrentUserId);
        }

        return View(wall);
    }

    private static WallUpdateModeView View(Wall wall) => new(wall.UpdateMode, wall.ActiveScanCaptureId is null);

    private static async Task<Wall> LoadAsync(BlocwerkDbContext db, Guid wallId) =>
        await db.Walls.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == wallId)
        ?? throw new UserFacingException("Wall not found");

    private async Task<BlocwerkDbContext> OpenForAdminAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, Action);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
