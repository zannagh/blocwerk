// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The settings card in one save (<see cref="UpdateSettingsAsync"/>), and the first photo of an empty wall under a
/// per-wall lock (<see cref="UploadFirstPhotoAsync"/>), for callers that cannot keep a circuit's state between calls
/// (the companion app).
/// </summary>
public partial class WallService
{
    /// <summary>The refusal when a first photo is uploaded to a wall that already has a photo or panels.</summary>
    public const string FirstPhotoTakenMessage = "This wall already has a photo or panels. Update it through the wall update.";

    /// <summary>One lock per wall for its first photo, so two uploads never both run hold detection. In process.</summary>
    private static readonly KeyedAsyncLocks<Guid> FirstPhotoLocks = new();

    public async Task<Wall> UpdateSettingsAsync(Guid wallId, WallSettingsUpdate update)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.UpdateSettings", wallId);
        try
        {
            if (update.ChangesKiosk)
            {
                // As SetAnonymousKioskSettingAsync / SetKioskKeyboardShortcutsAsync: never from the tablet itself.
                KioskGuard.EnsureNotKiosk(_kioskContext, "Changing kiosk permissions");
            }

            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
            var wall = await db.Walls.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == wallId)
                       ?? throw new InvalidOperationException("Wall not found");

            // Only what the caller sent is written: a field left out keeps whatever another save put there meanwhile.
            ApplySettings(wall, update, user.Id);
            await db.SaveChangesAsync();
            _logger.LogInformation("Wall {WallId} settings updated by {UserId}", wallId, user.Id);
            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public Task<Wall> UploadFirstPhotoAsync(Guid wallId, byte[] photo, string contentType) =>
        FirstPhotoLocks.RunAsync(
            wallId,
            async () =>
            {
                await using (var db = await _dbContextFactory.CreateDbContextAsync())
                {
                    var taken = await db.Walls.IgnoreQueryFilters().AsNoTracking()
                        .AnyAsync(w => w.Id == wallId && (w.PhotoContentType != null || db.WallPanels.Any(p => p.WallId == wallId)));
                    if (taken)
                    {
                        throw new UserFacingException(FirstPhotoTakenMessage);
                    }
                }

                return await UploadPhotoAsync(wallId, photo, contentType);
            },
            CancellationToken.None);

    private static void ApplySettings(Wall wall, WallSettingsUpdate update, Guid userId)
    {
        if (update.Name is { } name)
        {
            wall.Name = name;
        }

        if (update.Description is { } description)
        {
            wall.Description = description.Length == 0 ? null : description;
        }

        if (update.Angle is { } angle)
        {
            wall.Angle = angle;
        }

        if (update.UnderMaintenance is { } maintenance && maintenance != wall.UnderMaintenance)
        {
            wall.UnderMaintenance = maintenance;
            wall.MaintenanceByUserId = maintenance ? userId : null;
        }

        if (update.AllowAnonymousKiosk is { } anonymous)
        {
            wall.AllowAnonymousKioskSetting = anonymous;
        }

        if (update.AllowKioskKeyboardShortcuts is { } shortcuts)
        {
            wall.AllowKioskKeyboardShortcuts = shortcuts;
        }
    }
}
