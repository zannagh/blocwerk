using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Serves the bytes of one gallery item to the signed-in browser (or an anonymous share-token
/// viewer), mirroring the wall-photo routes: the three gallery sources live in different stores,
/// so uploads stream from the file store and the legacy photos come from their rows.
/// This is the cookie-authenticated UI route; machine callers use the API-key routes under /api.
/// It lives under /media so that HTTP routes stay out of the Blazor page route space (/walls,
/// /walls/{WallId}, /walls/{WallId}/shared/{ShareToken}) — and /media is not an
/// <see cref="ApiKeySurface"/> prefix, so an API key is never even forwarded here.
/// </summary>
public static class WallGalleryImageEndpoint
{
    /// <summary>The non-Blazor prefix the gallery byte routes are mounted under.</summary>
    public const string RoutePrefix = "/media/walls";

    public static void MapWallGalleryImages(this WebApplication app)
    {
        // The policy admits a signed-in human or an anonymous share-token viewer, and rejects an
        // API-key principal outright: machine callers read gallery bytes through
        // /api/walls/{wallId}/images/{source}/{id}/content, which checks the wall against the
        // key's own wall claim instead of against everything the key's owner may see.
        app.MapMethods(
                RoutePrefix + "/{wallId:guid}/gallery/{source}/{id:guid}",
                [HttpMethods.Get, HttpMethods.Head],
                HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage);
    }

    private static async Task<IResult> HandleAsync(
        Guid wallId,
        string source,
        Guid id,
        [FromQuery] string? token,
        [FromQuery(Name = "w")] int? w,
        ClaimsPrincipal user,
        HttpContext http,
        IWallService wallService,
        IWallImageService imageService,
        IWallImageStorage storage,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        [FromServices] IImageVariantCache variants,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal() || !ImageResponse.IsRenderableWidth(w))
        {
            return Results.NotFound();
        }

        if (!Enum.TryParse<WallGallerySource>(source, ignoreCase: true, out var gallerySource))
        {
            return Results.NotFound();
        }

        bool allowed;
        try
        {
            allowed = await HasWallAccessAsync(wallId, token, wallService, currentUserService, dbContextFactory, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }

        if (!allowed)
        {
            return Results.NotFound();
        }

        if (gallerySource == WallGallerySource.Uploaded)
        {
            // The row IS the metadata here — the bytes live on disk — so this path never had the
            // blob problem. It only lacked a validator: an uploaded image is written once and never
            // rewritten, so size and capture time identify it for good.
            var image = await imageService.GetImageAsync(id, ct);
            if (image == null || image.WallId != wallId)
            {
                return Results.NotFound();
            }

            var path = storage.ResolvePhysicalPath(image.StoragePath);
            if (path == null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            var uploadedVersion = ImageResponse.Key(image.SizeBytes, image.CapturedAt.UtcTicks, image.ContentType);

            // A rendition has to be buffered — it is produced in memory — so it takes the variant
            // path; without a width the file is still streamed, conditional and range handling
            // included, exactly as before.
            if (w is { } uploadedWidth)
            {
                var uploadedKey = new ImageVariantKey(ImageResponse.Key(id), uploadedVersion);

                return await ImageResponse.VariantAsync(
                    http,
                    ImageResponse.Etag(uploadedKey.Identity, uploadedKey.Version, uploadedWidth),
                    image.ContentType,
                    immutable: true,
                    () => variants.GetOrCreateAsync(
                        uploadedKey,
                        uploadedWidth,
                        async () => await File.ReadAllBytesAsync(path, ct),
                        ct));
            }

            // Handed to Results.File as an entity tag rather than run through ImageResponse: the
            // physical-file result already does conditional and range handling, and keeping it means
            // the file is still streamed instead of buffered.
            http.Response.Headers.CacheControl = ImageResponse.ImmutableCacheControl;

            return Results.File(
                path,
                image.ContentType,
                lastModified: image.CreatedAt,
                entityTag: EntityTagHeaderValue.Parse(
                    ImageResponse.Etag(id, image.SizeBytes, image.CapturedAt.UtcTicks, image.ContentType)),
                enableRangeProcessing: true);
        }

        // The two legacy sources are blobs on the wall/reset rows. Tag first, bytes only on a miss.
        var tag = await imageService.GetLegacyImageTagAsync(wallId, gallerySource, id, ct);
        if (tag is null)
        {
            return Results.NotFound();
        }

        return await ImageResponse.ServeAsync(
            http,
            variants,
            w,
            tag,
            tag.IsArchived,
            async () => (await imageService.GetLegacyImageContentAsync(wallId, gallerySource, id, ct))?.Data,
            wallId, source, id);
    }

    /// <summary>
    /// Same gate as the wall photo: a share token grants the anonymous path, otherwise the caller
    /// must be signed in and the wall must survive the membership query filter.
    /// </summary>
    private static async Task<bool> HasWallAccessAsync(
        Guid wallId,
        string? token,
        IWallService wallService,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(token))
        {
            var shared = await wallService.GetWallByShareTokenAsync(token);
            return shared != null && shared.Id == wallId;
        }

        // Deliberately not IWallService.GetWallAsync: a gallery page fires one request per
        // thumbnail and that call drags holds/boulders along. The query filter is the whole check.
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;

        return await db.Walls.AnyAsync(w => w.Id == wallId, ct);
    }
}
