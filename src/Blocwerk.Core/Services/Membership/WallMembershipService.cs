// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.Membership;

/// <summary>
/// <see cref="IWallMembershipService"/>: the guards (a kiosk never, an admin to remove, never the owner, never oneself by
/// removal), then <see cref="WallMembershipEnd"/> in one save.
/// </summary>
public sealed class WallMembershipService(
    IDbContextFactory<BlocwerkDbContext> contexts,
    ICurrentUserService currentUser,
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

        var wall = await db.Walls.FirstAsync(w => w.Id == wallId, ct);
        if (userId == caller.Id)
        {
            throw new MembershipRefusedException(MembershipRefusal.Self, "Use Leave to leave a wall yourself.");
        }

        await EndAsync(db, wall, userId, ct);
        logger.LogInformation("User {UserId} removed from wall {WallId} by {AdminId}", userId, wallId, caller.Id);
    }

    public async Task LeaveAsync(Guid wallId, CancellationToken ct = default)
    {
        var caller = await currentUser.GetCurrentUserAsync();
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = caller.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Leaving a wall");

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId, ct)
                   ?? throw new MembershipRefusedException(MembershipRefusal.NotAMember, "You are not a member of this wall.");
        await EndAsync(db, wall, caller.Id, ct);
        logger.LogInformation("User {UserId} left wall {WallId}", caller.Id, wallId);
    }

    private static async Task EndAsync(BlocwerkDbContext db, Wall wall, Guid userId, CancellationToken ct)
    {
        if (userId == wall.OwnerId)
        {
            throw new MembershipRefusedException(MembershipRefusal.Owner, "The wall's owner cannot leave or be removed.");
        }

        var membership = await db.WallMembers.FirstOrDefaultAsync(m => m.WallId == wall.Id && m.UserId == userId, ct)
                         ?? throw new MembershipRefusedException(MembershipRefusal.NotAMember, "That person is not a member of this wall.");

        await WallMembershipEnd.ApplyAsync(db, wall, membership, ct);
        await db.SaveChangesAsync(ct);
    }
}
