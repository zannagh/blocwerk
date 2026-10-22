// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// Who may read a wall's hashed assets. The same grants as the panel photo route: membership of the wall, a matching
/// share token, or the anonymous kiosk tablet bound to the wall. Unlike the gallery route a token is not exclusive: a
/// stale token falls through to membership, so a member browsing under an old share link still sees the wall.
/// </summary>
/// <remarks>
/// A kiosk session never reaches another wall, share token or not: the kiosk factory pins the wall query filter, and
/// the session's own wall is checked here as well, as <c>WallCaptureService.CanViewWallAsync</c> does, and so is its key:
/// a revoked or re-bound kiosk key gets nothing, anonymous or act-as (<see cref="KioskCacheRules.KeyIsLiveAsync"/>).
/// </remarks>
public static class AssetAccessGate
{
    /// <summary>The strongest grant the caller has on <paramref name="wallId"/>; membership wins over a token.</summary>
    public static async Task<AssetAccess> DecideAsync(
        BlocwerkDbContext db,
        Guid wallId,
        string? shareToken,
        ICurrentUserService currentUserService,
        IKioskContext? kioskContext,
        IKioskKeyValidator? kioskKeys,
        CancellationToken ct)
    {
        if (kioskContext is { IsKiosk: true }
            && (KioskViewing.ViewableWallId(kioskContext) != wallId || !await KioskCacheRules.KeyIsLiveAsync(kioskContext, wallId, kioskKeys, ct)))
        {
            return AssetAccess.None;
        }

        var user = await TryGetUserAsync(currentUserService);
        if (user is not null)
        {
            db.CurrentUserId = user.Id;
            if (await db.Walls.AnyAsync(w => w.Id == wallId, ct))
            {
                return AssetAccess.Member;
            }
        }

        if (!string.IsNullOrEmpty(shareToken))
        {
            db.CurrentUserId = Guid.Empty;
            if (await db.Walls.AnyAsync(w => w.Id == wallId && w.ShareToken == shareToken, ct))
            {
                return AssetAccess.ShareToken;
            }
        }

        return user is null && KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId)
            ? AssetAccess.KioskAnonymous
            : AssetAccess.None;
    }

    private static async Task<User?> TryGetUserAsync(ICurrentUserService currentUserService)
    {
        try
        {
            return await currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
