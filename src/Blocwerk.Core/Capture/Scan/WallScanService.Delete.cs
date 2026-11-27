// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>Delete: a site admin removes a received scan, its row and its folder, to make room under the limits.</summary>
public sealed partial class WallScanService
{
    public async Task<bool> MayDeleteAsync(CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return !KioskViewing.IsVisitor(kioskContext) && kioskContext?.IsKiosk != true && await AppAdminGuard.IsAppAdminAsync(db, user.Id, ct);
    }

    public async Task<bool> DeleteAsync(Guid wallId, Guid scanId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Removing an iPhone scan");
        if (KioskViewing.IsVisitor(kioskContext))
        {
            throw new UnauthorizedAccessException($"A visitor may not remove scans of wall {wallId}.");
        }

        await AppAdminGuard.EnsureAppAdminAsync(db, user.Id, ct);
        return await CaptureImportLocks.RunAsync(scanId, () => DeleteLockedAsync(db, wallId, scanId, ct), ct);
    }

    private async Task<bool> DeleteLockedAsync(BlocwerkDbContext db, Guid wallId, Guid scanId, CancellationToken ct)
    {
        var capture = await db.WallCaptures.IgnoreQueryFilters().FirstOrDefaultAsync(
                          c => c.Id == scanId && c.WallId == wallId && c.GeometryMode == WallCaptureGeometryMode.Device, ct)
                      ?? throw new ScanUploadException(ScanUploadException.NotFound, "There is no scan with this id on this wall.");
        if (capture.Status != WallCaptureStatus.Received)
        {
            throw new ScanUploadException(
                ScanUploadException.Conflict, "Only a received scan can be removed here; an unfinished upload is removed by retention.");
        }

        db.WallCaptures.Remove(capture);
        await db.SaveChangesAsync(ct);
        ScanFileTables.Forget(scanId);
        if (storage.HasFolder)
        {
            // Left to the scan retention (a folder whose capture is gone) when it cannot go now.
            try
            {
                storage.Delete(scanId);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not remove the folder of scan {ScanId}; the scan retention removes it", scanId);
            }
        }

        logger.LogInformation("Scan {ScanId} on wall {WallId}: removed by user {UserId}", scanId, wallId, db.CurrentUserId);
        return true;
    }
}
