// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Services.Membership;

/// <summary>
/// What ending one membership changes, following account deletion's rule for a person who leaves every wall
/// (<c>AccountDeletionService.ErasePersonalRowsAsync</c>), narrowed to this one wall.
/// </summary>
/// <remarks>
/// <para>
/// Gone: the membership row, and with it the kiosk PIN and kiosk consent; the person's private lists on this wall's
/// boulders (favourites, projects); their home-wall choice when it was this wall; the visitor links they made for this
/// wall, so a guest they invited loses the visit too. An open climbing session on this wall is ended. Wall keys (tablet,
/// sensor) the person minted for this wall move to the owner under a neutral name, so the gym's hardware keeps working.
/// </para>
/// <para>
/// Beta clips they uploaded to this wall: gone on a removal, and on leaving unless they chose to leave them. The clips
/// show the person climbing, which is why account deletion removes them; a removed person can no longer reach the wall
/// to take them down, so a removal does what deletion does. Someone leaving decides for themselves.
/// </para>
/// <para>
/// Kept, as account deletion keeps it: what other members see, i.e. boulders they set, setter credits, comments,
/// ratings, attempts and ticks, and the wall's history. Unlike account deletion, the person still exists and keeps their
/// name on it. Their training history and activities are not wall data and are untouched.
/// </para>
/// <para>
/// The invite (join) token is replaced, so a link the person still holds no longer joins. The public share token stays:
/// a share link only shows the wall, and others may have it. The tracked rows save together, so the feed sees them:
/// <c>member.changed</c> for the wall, <c>access.revoked</c> and <c>walls.changed</c> for the person's own devices;
/// deleted clips write their <c>video.deleted</c> in the same transaction.
/// </para>
/// </remarks>
internal static class WallMembershipEnd
{
    public static async Task<IReadOnlyList<MembershipVideoFile>> ApplyAsync(
        BlocwerkDbContext db,
        Wall wall,
        WallMember membership,
        bool deleteBetaVideos,
        CancellationToken ct)
    {
        var userId = membership.UserId;
        db.WallMembers.Remove(membership);

        var boulders = db.Boulders.Where(b => b.WallId == wall.Id).Select(b => b.Id);
        db.BoulderFavorites.RemoveRange(await db.BoulderFavorites.Where(f => f.UserId == userId && boulders.Contains(f.BoulderId)).ToListAsync(ct));
        db.BoulderProjects.RemoveRange(await db.BoulderProjects.Where(p => p.UserId == userId && boulders.Contains(p.BoulderId)).ToListAsync(ct));
        await db.WallVisitLinks.Where(l => l.WallId == wall.Id && l.CreatedByUserId == userId).ExecuteDeleteAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var session in await db.ClimbingSessions.Where(s => s.UserId == userId && s.WallId == wall.Id && s.EndedAt == null).ToListAsync(ct))
        {
            session.EndedAt = now;
        }

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        if (user.HomeWallId == wall.Id)
        {
            user.HomeWallId = null;
        }

        await HandWallKeysToOwnerAsync(db, wall, userId, ct);
        wall.JoinToken = WallTokens.Create();
        return deleteBetaVideos ? await DeleteBetaVideosAsync(db, wall.Id, userId, ct) : [];
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

    // In bulk, as the clip's own delete does, so no clip is loaded into memory; the files go after the commit.
    private static async Task<IReadOnlyList<MembershipVideoFile>> DeleteBetaVideosAsync(BlocwerkDbContext db, Guid wallId, Guid userId, CancellationToken ct)
    {
        var clips = db.BetaVideos.IgnoreQueryFilters().Where(v => v.UploadedByUserId == userId && v.Boulder.WallId == wallId);
        var files = await clips.Select(v => new MembershipVideoFile(v.Id, v.StoragePath)).ToListAsync(ct);
        if (files.Count > 0)
        {
            await FeedVideoBulk.DeleteAsync(db, clips, NullLogger.Instance, ct);
        }

        return files;
    }
}
