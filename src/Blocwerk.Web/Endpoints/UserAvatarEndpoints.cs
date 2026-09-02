using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Serves a user's avatar image bytes to a browser. Avatars are public profile data (rendered next
/// to a member's name across cards, comments and leaderboards), so ANY caller — signed in or not —
/// may fetch any user's avatar; 404 when the user has none.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately anonymous, not an oversight. Two shipped features render avatars to a caller with
/// no session: an anonymous share-token boulder page shows the activity feed's per-row avatars
/// (<c>BoulderActivityFeed</c>), and the kiosk user picker shows a face for
/// every wall member BEFORE anybody has picked themselves (<c>KioskUsers</c>). Requiring
/// authorization here would break both with broken-image icons.
/// </para>
/// <para>
/// What that concedes is an existence-and-size oracle on a user id: an anonymous caller who already
/// knows a GUID learns whether that user exists with an avatar, and its byte length via the ETag.
/// Guessing the GUID is the barrier, and the same fact is readable from any wall page the caller
/// can already see. Closing it properly means scoping the route to a wall/share context rather than
/// bolting on <c>RequireAuthorization</c>.
/// </para>
/// <para>
/// The route sits under <c>/api</c>, which is a prefix on which an API key is allowed to
/// authenticate, but this is a browser route: an API-key principal is rejected outright, exactly as
/// the wall photo routes do (see <see cref="WallPhotoEndpoints"/>).
/// </para>
/// </remarks>
public static class UserAvatarEndpoints
{
    public static void MapUserAvatars(this WebApplication app)
    {
        app.MapMethods("/api/users/{userId:guid}/avatar", [HttpMethods.Get, HttpMethods.Head], async (
            Guid userId,
            ClaimsPrincipal user,
            HttpContext http,
            IDbContextFactory<BlocwerkDbContext> dbContextFactory) =>
        {
            if (user.IsApiKeyPrincipal())
            {
                return Results.NotFound();
            }

            await using var db = await dbContextFactory.CreateDbContextAsync();

            // Metadata first, bytes second. Length is a server-side length(bytea), so a browser that
            // already holds this avatar is answered with a 304 off this row alone — an avatar is
            // rendered beside every name on a page, and these blobs run to megabytes.
            var avatar = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId && u.AvatarImage != null)
                .Select(u => new { Length = u.AvatarImage!.Length, u.AvatarContentType })
                .FirstOrDefaultAsync();

            if (avatar is null || avatar.Length == 0)
            {
                return Results.NotFound();
            }

            // User carries no avatar-updated column, so the validator is the stored size and type.
            // Replacing an avatar with a different image that re-encodes to the identical byte count
            // is the one case this would miss; an AvatarUpdatedAt column would close it.
            var etag = ImageResponse.Etag(userId, avatar.Length, avatar.AvatarContentType);

            return await ImageResponse.ConditionalAsync(
                http,
                etag,
                avatar.AvatarContentType,
                avatar.Length,
                immutable: false,
                () => db.Users
                    .AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => u.AvatarImage)
                    .FirstOrDefaultAsync());
        }).DenyApiKeyPrincipals();
    }
}
