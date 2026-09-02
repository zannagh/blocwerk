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
/// <para>
/// Each route resolves a <see cref="WallPhotoTag"/> first — metadata only, under the same gate as
/// the byte call it precedes — and hands the blob load to <see cref="ImageResponse"/> as a
/// callback, so a revalidation that ends in 304 never reads the photo out of Postgres.
/// </para>
/// </remarks>
public static class WallPhotoEndpoints
{
    public static void MapWallPhotos(this WebApplication app)
    {
        app.MapMethods("/api/walls/{wallId:guid}/photo", [HttpMethods.Get, HttpMethods.Head], async (
            Guid wallId,
            [FromQuery] string? token,
            [FromQuery(Name = "w")] int? w,
            ClaimsPrincipal user,
            HttpContext http,
            [FromServices] IWallService wallService,
            [FromServices] IImageVariantCache variants) =>
        {
            if (user.IsApiKeyPrincipal() || !ImageResponse.IsRenderableWidth(w))
            {
                return Results.NotFound();
            }

            var tag = await wallService.GetPhotoTagAsync(wallId, token);
            if (tag is null)
            {
                return Results.NotFound();
            }

            return await ImageResponse.ServeAsync(
                http,
                variants,
                w,
                tag,
                immutable: false,
                () => string.IsNullOrEmpty(token)
                    ? wallService.GetPhotoAsync(wallId)
                    : wallService.GetPhotoByShareTokenAsync(wallId, token),
                wallId, "live");
        }).DenyApiKeyPrincipals();

        app.MapMethods("/api/walls/{wallId:guid}/photo/{generation:int}", [HttpMethods.Get, HttpMethods.Head], async (
            Guid wallId,
            int generation,
            [FromQuery] string? token,
            [FromQuery(Name = "w")] int? w,
            ClaimsPrincipal user,
            HttpContext http,
            [FromServices] IWallService wallService,
            [FromServices] IImageVariantCache variants) =>
        {
            if (user.IsApiKeyPrincipal() || !ImageResponse.IsRenderableWidth(w))
            {
                return Results.NotFound();
            }

            var tag = await wallService.GetPhotoTagForGenerationAsync(wallId, token, generation);
            if (tag is null)
            {
                return Results.NotFound();
            }

            // A retired generation's photo is archived on its reset row and never rewritten, so this
            // route is content-addressed and the browser is told it need not ask again. A generation
            // at or above the current one resolves to the LIVE photo, which is mutable — hence the
            // flag off the tag rather than off the route shape.
            return await ImageResponse.ServeAsync(
                http,
                variants,
                w,
                tag,
                tag.IsArchived,
                async () =>
                {
                    var photo = string.IsNullOrEmpty(token)
                        ? await wallService.GetPhotoForGenerationAsync(wallId, generation)
                        : await wallService.GetPhotoForGenerationByShareTokenAsync(wallId, token, generation);
                    return photo?.Photo;
                },
                wallId, generation);
        }).DenyApiKeyPrincipals();

        app.MapMethods("/api/walls/{wallId:guid}/staged-photo", [HttpMethods.Get, HttpMethods.Head], async (
            Guid wallId,
            [FromQuery(Name = "w")] int? w,
            ClaimsPrincipal user,
            HttpContext http,
            [FromServices] IWallService wallService,
            [FromServices] IImageVariantCache variants) =>
        {
            if (user.IsApiKeyPrincipal() || !ImageResponse.IsRenderableWidth(w))
            {
                return Results.NotFound();
            }

            var tag = await wallService.GetStagedPhotoTagAsync(wallId);
            if (tag is null)
            {
                return Results.NotFound();
            }

            return await ImageResponse.ServeAsync(
                http,
                variants,
                w,
                tag,
                immutable: false,
                () => wallService.GetStagedPhotoAsync(wallId),
                wallId, "staged");
        }).DenyApiKeyPrincipals();
    }
}
