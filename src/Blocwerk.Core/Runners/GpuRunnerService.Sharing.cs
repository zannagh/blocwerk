// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Sharing a runner with other walls takes two people: a SITE admin offers it (anyone can create a wall and thus a
/// runner, so a runner's owner alone cannot), and the receiving wall's admin approves that one runner.
/// </summary>
public sealed partial class GpuRunnerService
{
    public async Task SetSharedAsync(Guid runnerId, bool shared)
    {
        ApiKeySessionGuard.EnsureNotApiKeySession(apiKeySession);
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
        await AppAdminGuard.EnsureAppAdminAsync(db, user.Id, CancellationToken.None);
        var runner = await db.GpuRunners.FirstOrDefaultAsync(r => r.Id == runnerId && r.RevokedAt == null)
                     ?? throw new UserFacingException("Runner not found");
        runner.SharedWithOtherWalls = shared;
        if (!shared)
        {
            // A later re-share needs fresh approvals: consent was given to the runner as it was offered then.
            db.GpuRunnerApprovals.RemoveRange(await db.GpuRunnerApprovals.Where(a => a.RunnerId == runnerId).ToListAsync());
        }

        await db.SaveChangesAsync();
        logger.LogInformation("3D runner {RunnerId} shared with other walls: {Shared} (by site admin {UserId})", runner.Id, shared, user.Id);
    }

    public async Task SetRunnerApprovalAsync(Guid wallId, Guid runnerId, bool approve)
    {
        ApiKeySessionGuard.EnsureNotApiKeySession(apiKeySession);
        var (db, userId) = await OpenForWallAdminAsync(wallId);
        await using (db)
        {
            var row = await db.GpuRunnerApprovals.FirstOrDefaultAsync(a => a.WallId == wallId && a.RunnerId == runnerId);
            if (approve && row is null)
            {
                if (!await db.GpuRunners.AnyAsync(r => r.Id == runnerId && r.RevokedAt == null && r.SharedWithOtherWalls))
                {
                    throw new UserFacingException("That runner is not offered to other walls.");
                }

                db.GpuRunnerApprovals.Add(new GpuRunnerApproval { WallId = wallId, RunnerId = runnerId, ApprovedByUserId = userId });
            }
            else if (!approve && row is not null)
            {
                db.GpuRunnerApprovals.Remove(row);
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "Wall {WallId} approves shared 3D runner {RunnerId}: {Approve} (by {UserId})", wallId, runnerId, approve, userId);
        }
    }
}
