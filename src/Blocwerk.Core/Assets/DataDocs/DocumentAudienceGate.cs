// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Who may read a wall's data documents, and as which <see cref="DocumentAudience"/>. The grant is the feed's
/// (<see cref="FeedAccessGate"/>), because a document and the feed replayed after it must agree on who sees what; the
/// class is then decided <b>kiosk first</b>, whoever the tablet acts as, so a tablet acting as a setter reads the kiosk
/// class and never a draft (unlike <c>AssetAccessGate</c>, which answers <c>Member</c> for an acting member).
/// </summary>
/// <remarks>
/// The feed gate is kiosk-first too, so a tablet's viewer never carries the Setters audience; this class checks the
/// kiosk on its own anyway, so the order holds even if the feed's rule moves. A refusal for a wall that does not exist
/// is told apart (404) only after the caller was found to be somebody: a signed-out caller with no token is 401 first.
/// </remarks>
public static class DocumentAudienceGate
{
    /// <summary>The caller's class on <paramref name="wallId"/>, or why it has none.</summary>
    public static async Task<DocumentAccess> DecideAsync(
        BlocwerkDbContext db, Guid wallId, string? shareToken, FeedSession session, CancellationToken ct)
    {
        var access = await FeedAccessGate.ForWallAsync(db, wallId, shareToken, session, ct);
        if (access.Viewer is not { } viewer)
        {
            var missing = access.Refusal == FeedAccessRefusal.NoAccess && !session.Caller.IsApiKey
                && !await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct);
            return DocumentAccess.Refuse(access.Refusal, missing);
        }

        if (await IsKioskAsync(session.Kiosk))
        {
            return DocumentAccess.Grant(DocumentAudience.Kiosk);
        }

        return DocumentAccess.Grant(DocumentAudiences.Of(viewer));
    }

    private static async Task<bool> IsKioskAsync(IKioskContext? kiosk)
    {
        if (kiosk is null)
        {
            return false;
        }

        await kiosk.InitializeAsync();
        return kiosk.IsKiosk;
    }
}
