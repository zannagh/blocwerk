// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services.Membership;

/// <summary>
/// What ending one membership changes, following account deletion's rule for a person who leaves every wall
/// (<c>AccountDeletionService.ErasePersonalRowsAsync</c>), narrowed to this one wall.
/// </summary>
/// <remarks>
/// <para>
/// Gone: the membership row, and with it the kiosk PIN and kiosk consent; the person's private lists on this wall's
/// boulders (favourites, projects); their home-wall choice when it was this wall. An open climbing session on this wall
/// is ended. Wall keys (tablet, sensor) the person minted for this wall move to the owner under a neutral name, so the
/// gym's hardware keeps working.
/// </para>
/// <para>
/// Kept, as account deletion keeps it: everything other members see, i.e. boulders they set, setter credits, comments,
/// ratings, attempts and ticks, beta videos and the wall's history. Unlike account deletion, the person still exists and
/// keeps their name on it. Their training history and activities are not wall data and are untouched.
/// </para>
/// <para>
/// The invite token is replaced, so a link the person still holds no longer joins. Everything is tracked and saved
/// together, so the feed sees it: <c>member.changed</c> for the wall, <c>access.revoked</c> and <c>walls.changed</c> for
/// the person's own devices.
/// </para>
/// </remarks>
internal static class WallMembershipEnd
{
    public static async Task ApplyAsync(BlocwerkDbContext db, Wall wall, WallMember membership, CancellationToken ct)
    {
        var userId = membership.UserId;
        db.WallMembers.Remove(membership);

        var boulders = db.Boulders.Where(b => b.WallId == wall.Id).Select(b => b.Id);
        db.BoulderFavorites.RemoveRange(await db.BoulderFavorites.Where(f => f.UserId == userId && boulders.Contains(f.BoulderId)).ToListAsync(ct));
        db.BoulderProjects.RemoveRange(await db.BoulderProjects.Where(p => p.UserId == userId && boulders.Contains(p.BoulderId)).ToListAsync(ct));

        var now = DateTimeOffset.UtcNow;
        foreach (var session in await db.ClimbingSessions.Where(s => s.UserId == userId && s.WallId == wall.Id && s.EndedAt == null).ToListAsync(ct))
        {
            session.EndedAt = now;
        }

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (user.HomeWallId == wall.Id)
        {
            user.HomeWallId = null;
        }

        await HandWallKeysToOwnerAsync(db, wall, userId, ct);
        wall.ShareToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }

    // As account deletion does: a wall key authorises the wall, not the person, so it stays and changes hands; its name is
    // the person's free text and is replaced.
    private static async Task HandWallKeysToOwnerAsync(BlocwerkDbContext db, Wall wall, Guid userId, CancellationToken ct)
    {
        var keys = await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.UserId == userId && k.WallId == wall.Id && k.Scope != ApiKeyScope.User)
            .ToListAsync(ct);
        foreach (var key in keys)
        {
            key.UserId = wall.OwnerId;
            key.Name = $"{key.Scope} key {key.Prefix}";
        }
    }
}
