// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.Mine;

/// <summary>
/// Who may read their own marks of a wall (<c>/api/me/walls/{w}/mine</c>). Two gates, both of which must grant:
/// <list type="number">
/// <item><description>The caller's own rows (<see cref="FeedAccessGate.ForSelfAsync"/>): a signed-in person only. A kiosk
/// tablet, whoever it acts as, and an API key are refused (403), nobody is 401, and an erased, Ghost or merged account is
/// 410, as on <c>/api/me/changes</c>. Per-user data never reaches a shared tablet.</description></item>
/// <item><description>The wall (<see cref="DocumentAudienceGate"/>, kiosk first): the reader's class must be one that sees
/// its own marks on the wall page online (<see cref="DocumentAudiences.SeesMine"/>), so a signed-in non-member reading a
/// share link gets 403, as its page shows no sends and no favourites. No share token is taken: it never grants
/// this.</description></item>
/// </list>
/// </summary>
public static class MineAccessGate
{
    /// <summary>The reader and their class on <paramref name="wallId"/>, or why there is none.</summary>
    public static async Task<MineAccess> DecideAsync(BlocwerkDbContext db, Guid wallId, FeedSession session, CancellationToken ct)
    {
        var self = await FeedAccessGate.ForSelfAsync(session);
        if (self.Viewer is null || self.CallerUserId is not { } userId)
        {
            return MineAccess.Refuse(self.Refusal);
        }

        var wall = await DocumentAudienceGate.DecideAsync(db, wallId, shareToken: null, session, ct);
        if (wall.Audience is not { } audience)
        {
            return MineAccess.Refuse(wall.Refusal, wall.WallMissing);
        }

        return DocumentAudiences.SeesMine(audience)
            ? MineAccess.Grant(userId, audience)
            : MineAccess.Refuse(FeedAccessRefusal.NoAccess);
    }
}
