// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Who may read the feed over HTTP (design §5.8), and as which <see cref="FeedViewer"/>: the same audiences the circuits
/// subscribe with (<c>CircuitFeedViewer</c>), so a catch-up page never carries a row the hub would not have delivered.
/// <list type="bullet">
/// <item><description>An API key never reads the feed: it is a device secret, and the feed spans every kind of the wall.</description></item>
/// <item><description>A kiosk tablet reads its own wall only, always as <see cref="FeedViewer.Kiosk"/> whoever it is acting
/// as, so a member's own rows never reach the shared tablet, and only while its key is live; it never reads
/// <c>/api/me</c>.</description></item>
/// <item><description>A signed-in user reads as their role (<see cref="FeedViewers.ForUserAsync"/>, read from the database).</description></item>
/// <item><description>Anyone else, a signed-in non-member included, needs the wall's current share token and reads as
/// <see cref="FeedViewer.Anonymous"/>. Membership wins over a token, as on the asset routes.</description></item>
/// </list>
/// </summary>
public static class FeedAccessGate
{
    /// <summary>The viewer for one wall's feed, or why there is none.</summary>
    public static async Task<FeedAccess> ForWallAsync(
        BlocwerkDbContext db,
        Guid wallId,
        string? shareToken,
        FeedSession session,
        CancellationToken cancellationToken)
    {
        if (session.Caller.IsApiKey)
        {
            return FeedAccess.Refuse(FeedAccessRefusal.NoAccess);
        }

        if (await IsKioskAsync(session.Kiosk))
        {
            return await KioskMayReadAsync(session, wallId, cancellationToken)
                ? FeedAccess.Grant(FeedViewer.Kiosk(), null)
                : FeedAccess.Refuse(FeedAccessRefusal.NoAccess);
        }

        var (user, gone) = await ResolveAsync(session.CurrentUser, session.Caller);
        if (gone)
        {
            return FeedAccess.Refuse(FeedAccessRefusal.AccountGone);
        }

        if (user is not null && await FeedViewers.ForUserAsync(db, wallId, user.Id, cancellationToken) is { } viewer)
        {
            return FeedAccess.Grant(viewer, user.Id);
        }

        var hasToken = !string.IsNullOrEmpty(shareToken);
        if (hasToken && await db.Walls.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(w => w.Id == wallId && w.ShareToken == shareToken, cancellationToken))
        {
            return FeedAccess.Grant(FeedViewer.Anonymous(), user?.Id);
        }

        return FeedAccess.Refuse(user is null && !hasToken ? FeedAccessRefusal.Unauthenticated : FeedAccessRefusal.NoAccess);
    }

    /// <summary>
    /// The viewer for the caller's own rows across walls (<c>/api/me/changes</c>): a signed-in person only, never a kiosk
    /// tablet (per-user data is never stored on one, design §7.2).
    /// </summary>
    public static async Task<FeedAccess> ForSelfAsync(FeedSession session)
    {
        if (session.Caller.IsApiKey || await IsKioskAsync(session.Kiosk))
        {
            return FeedAccess.Refuse(FeedAccessRefusal.NoAccess);
        }

        var (user, gone) = await ResolveAsync(session.CurrentUser, session.Caller);
        if (gone)
        {
            return FeedAccess.Refuse(FeedAccessRefusal.AccountGone);
        }

        return user is null
            ? FeedAccess.Refuse(FeedAccessRefusal.Unauthenticated)
            : FeedAccess.Grant(FeedViewer.Member(user.Id), user.Id);
    }

    // The tablet's own wall, and its key still live: a revoked or re-bound kiosk key stops reading on its next request,
    // and an open stream on its next access check, instead of at cookie expiry. No validator means no grant.
    private static async Task<bool> KioskMayReadAsync(FeedSession session, Guid wallId, CancellationToken cancellationToken) =>
        KioskViewing.AllowsAnonymousViewOf(session.Kiosk, wallId)
        && session.Kiosk?.KioskApiKeyId is { } keyId && keyId != Guid.Empty
        && session.KioskKeys is { } keys
        && await keys.IsKeyValidAsync(keyId, wallId, cancellationToken);

    private static async Task<bool> IsKioskAsync(IKioskContext? kiosk)
    {
        if (kiosk is null)
        {
            return false;
        }

        await kiosk.InitializeAsync(); // idempotent; IsKiosk is only meaningful once the session's kiosk state is primed
        return kiosk.IsKiosk;
    }

    // An authenticated principal whose claims resolve to nobody who may sign in (an erased tombstone, the Ghost row, an
    // account merged away) is "account gone", not "signed out": the device holds that account's data and must drop it.
    private static async Task<(User? User, bool Gone)> ResolveAsync(ICurrentUserService currentUser, FeedCaller caller)
    {
        try
        {
            return (await currentUser.GetCurrentUserAsync(), false);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, caller.HasIdentity);
        }
    }
}
