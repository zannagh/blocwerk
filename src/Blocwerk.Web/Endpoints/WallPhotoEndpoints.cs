using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The wall photo bytes for the browser: the current photo, the photo as it looked at a given
/// generation (so historic boulders render against the wall they were actually set on), and the
/// staged photo of a wall update in progress.
/// </summary>
/// <remarks>
/// These routes live under <c>/api/walls</c>, which is a prefix on which an API key is allowed to
/// authenticate, but they are NOT machine routes: they gate on what the signed-in caller may see
/// (or on a share token), never on the wall the key was issued for. An API-key principal is
/// therefore rejected outright — otherwise a key for wall A would read the photos of every wall
/// its owner belongs to. Machine callers use <c>/api/walls/{wallId}/images/…</c>, which compares
/// the route's wall against the key's own wall claim.
/// </remarks>
public static class WallPhotoEndpoints
{
    public static void MapWallPhotos(this WebApplication app)
    {
        app.MapGet("/api/walls/{wallId:guid}/photo", async (
            Guid wallId,
            [FromQuery] string? token,
            ClaimsPrincipal user,
            [FromServices] IWallService wallService) =>
        {
            if (user.IsApiKeyPrincipal())
            {
                return Results.NotFound();
            }

            byte[]? photo;
            if (!string.IsNullOrEmpty(token))
            {
                photo = await wallService.GetPhotoByShareTokenAsync(wallId, token);
            }
            else
            {
                photo = await wallService.GetPhotoAsync(wallId);
            }

            return photo == null ? Results.NotFound() : Results.File(photo, "image/jpeg");
        }).DenyApiKeyPrincipals();

        app.MapGet("/api/walls/{wallId:guid}/photo/{generation:int}", async (
            Guid wallId,
            int generation,
            [FromQuery] string? token,
            ClaimsPrincipal user,
            [FromServices] IWallService wallService) =>
        {
            if (user.IsApiKeyPrincipal())
            {
                return Results.NotFound();
            }

            var photo = string.IsNullOrEmpty(token)
                ? await wallService.GetPhotoForGenerationAsync(wallId, generation)
                : await wallService.GetPhotoForGenerationByShareTokenAsync(wallId, token, generation);

            return photo == null
                ? Results.NotFound()
                : Results.File(photo.Photo, photo.ContentType ?? "image/jpeg");
        }).DenyApiKeyPrincipals();

        app.MapGet("/api/walls/{wallId:guid}/staged-photo", async (
            Guid wallId,
            ClaimsPrincipal user,
            [FromServices] IWallService wallService) =>
        {
            if (user.IsApiKeyPrincipal())
            {
                return Results.NotFound();
            }

            var photo = await wallService.GetStagedPhotoAsync(wallId);
            return photo == null ? Results.NotFound() : Results.File(photo, "image/jpeg");
        }).DenyApiKeyPrincipals();
    }
}
