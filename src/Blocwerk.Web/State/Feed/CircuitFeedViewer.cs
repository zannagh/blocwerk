// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// The circuit's <see cref="FeedViewer"/>. A kiosk tablet gets <see cref="FeedViewer.Kiosk"/> with nobody picked and
/// <see cref="FeedViewer.KioskActing"/> acting as a member, and only on its own wall: neither carries the acting member's
/// id, which must never reach a subscription, or their own rows
/// (<c>tick.mine</c>, <c>rating.mine</c>, <c>favorite.changed</c>) would reach the shared tablet. A signed-in user gets the
/// audiences of their role, read from the database (<see cref="FeedViewers.ForUserAsync"/>).
/// </summary>
public sealed class CircuitFeedViewer(
    IKioskContext kiosk, ICurrentUserService currentUser, RootDbContextFactory contexts) : ICircuitFeedViewer
{
    public async Task<FeedViewer?> ResolveAsync(Guid wallId)
    {
        await kiosk.InitializeAsync(); // idempotent; never read IsKiosk before the circuit's kiosk state is primed
        Guid? userId;
        try
        {
            userId = (await currentUser.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            userId = null;
        }

        if (kiosk.IsKiosk)
        {
            return kiosk.KioskWallId != wallId ? null
                : userId is null ? FeedViewer.Kiosk()
                : FeedViewer.KioskActing();
        }

        if (userId is null)
        {
            return null;
        }

        await using var db = contexts.CreateDbContext();
        return await FeedViewers.ForUserAsync(db, wallId, userId.Value);
    }
}
