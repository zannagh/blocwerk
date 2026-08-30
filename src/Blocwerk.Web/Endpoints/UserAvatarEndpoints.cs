using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Serves a user's avatar image bytes to the signed-in browser. Avatars are public profile data
/// (rendered next to a member's name across cards, comments and leaderboards), so any signed-in
/// caller may fetch any user's avatar; 404 when the user has none.
/// </summary>
/// <remarks>
/// The route sits under <c>/api</c>, which is a prefix on which an API key is allowed to
/// authenticate, but this is a browser route: an API-key principal is rejected outright, exactly as
/// the wall photo routes do (see <see cref="WallPhotoEndpoints"/>).
/// </remarks>
public static class UserAvatarEndpoints
{
    public static void MapUserAvatars(this WebApplication app)
    {
        app.MapGet("/api/users/{userId:guid}/avatar", async (
            Guid userId,
            ClaimsPrincipal user,
            IDbContextFactory<BlocwerkDbContext> dbContextFactory) =>
        {
            if (user.IsApiKeyPrincipal())
            {
                return Results.NotFound();
            }

            await using var db = await dbContextFactory.CreateDbContextAsync();
            var avatar = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.AvatarImage, u.AvatarContentType })
                .FirstOrDefaultAsync();

            if (avatar?.AvatarImage is not { Length: > 0 } bytes)
            {
                return Results.NotFound();
            }

            return Results.File(bytes, avatar.AvatarContentType ?? "image/jpeg");
        }).DenyApiKeyPrincipals();
    }
}
