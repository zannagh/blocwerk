// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.Membership;

/// <summary>
/// <see cref="IWallMembershipService"/>: the guards (a kiosk never, an admin to remove, never the owner, never oneself by
/// removal), then <see cref="WallMembershipEnd"/> in one transaction, with the member rows locked
/// (<see cref="MembershipRowLock"/>) and the admin check taken again under the lock.
/// </summary>
public sealed class WallMembershipService(
    IDbContextFactory<BlocwerkDbContext> contexts,
    ICurrentUserService currentUser,
    IActivityLogService activityLog,
    IBetaVideoStorage videoStorage,
    ILogger<WallMembershipService> logger,
    IKioskContext? kioskContext = null) : IWallMembershipService
{
    public async Task RemoveMemberAsync(Guid wallId, Guid userId, CancellationToken ct = default)
    {
        var caller = await currentUser.GetCurrentUserAsync();
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = caller.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Removing a member");
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, caller.Id, ct);
        if (userId == caller.Id)
        {
            throw new MembershipRefusedException(MembershipRefusal.Self, "Use Leave to leave a wall yourself.");
        }

        // A removed person can no longer reach the wall to take their clips down, and the clips show them climbing:
        // personal data, which account deletion removes too. So removal deletes them (see WallMembershipEnd).
        var files = await EndAsync(db, new MembershipEnding(wallId, userId, caller.Id, DeleteBetaVideos: true), ct);
        DeleteVideoFiles(userId, files);
        await activityLog.LogAboutMemberAsync(wallId, ActivityType.MemberRemoved, userId);
        logger.LogInformation("User {UserId} removed from wall {WallId} by {AdminId}", userId, wallId, caller.Id);
    }

    public async Task LeaveAsync(Guid wallId, bool deleteBetaVideos, CancellationToken ct = default)
    {
        var caller = await currentUser.GetCurrentUserAsync();
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = caller.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Leaving a wall");

        var files = await EndAsync(db, new MembershipEnding(wallId, caller.Id, RemovedBy: null, deleteBetaVideos), ct);
        DeleteVideoFiles(caller.Id, files);
        await activityLog.LogAboutMemberAsync(wallId, ActivityType.MemberLeft, caller.Id);
        logger.LogInformation("User {UserId} left wall {WallId}", caller.Id, wallId);
    }

    private static async Task<IReadOnlyList<MembershipVideoFile>> EndAsync(BlocwerkDbContext db, MembershipEnding ending, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MembershipRowLock.LockAsync(db, ending.WallId, ending.LockedUsers, ct);

        // Past the query filters: the Wall and member filters gate on the caller's own member row, which a legacy owner
        // (an owner with no member row) does not have. Who may act was checked above and is checked again just below.
        var wall = await db.Walls.IgnoreQueryFilters().WithoutPhotos().FirstOrDefaultAsync(w => w.Id == ending.WallId, ct)
                   ?? throw new MembershipRefusedException(MembershipRefusal.NotAMember, "You are not a member of this wall.");
        db.Attach(wall);

        if (ending.RemovedBy is { } adminId && !await WallAdminGuard.IsWallAdminAsync(db, wall.Id, adminId, ct))
        {
            throw new UnauthorizedAccessException($"User {adminId} is not an admin of wall {wall.Id} any more.");
        }

        if (ending.UserId == wall.OwnerId)
        {
            throw new MembershipRefusedException(MembershipRefusal.Owner, "The wall's owner cannot leave or be removed.");
        }

        var membership = await db.WallMembers.IgnoreQueryFilters()
                             .FirstOrDefaultAsync(m => m.WallId == wall.Id && m.UserId == ending.UserId, ct)
                         ?? throw new MembershipRefusedException(MembershipRefusal.NotAMember, "That person is not a member of this wall.");

        var files = await WallMembershipEnd.ApplyAsync(db, wall, membership, ending.DeleteBetaVideos, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return files;
    }

    // After the commit, as account deletion does: a file that cannot be removed is an orphan to sweep up, not a failure.
    private void DeleteVideoFiles(Guid userId, IReadOnlyList<MembershipVideoFile> files)
    {
        foreach (var file in files)
        {
            try
            {
                videoStorage.Delete(file.StoragePath);
                videoStorage.DeleteHlsDirectory(file.VideoId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Beta clip {VideoId} of {UserId} could not be removed from storage; it is orphaned", file.VideoId, userId);
            }
        }
    }
}
