using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
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

    internal static async Task<IResult> HandleAsync(
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
        [FromServices] IKioskContext kioskContext,
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

        if (!await HasWallAccessAsync(
                wallId, token, wallService, currentUserService, dbContextFactory, kioskContext, ct))
        {
            return Results.NotFound();
        }

        if (gallerySource == WallGallerySource.Uploaded)
        {
            // The row IS the metadata here — the bytes live on disk — so this path never had the
            // blob problem. It only lacked a validator: size and capture time identify the file. The
            // privacy pass rewrites it in place and moves the size, so only a url naming the current
            // size may be cached immutable; an unversioned one revalidates against the ETag.
            var image = await imageService.GetImageAsync(id, ct);
            if (image == null || image.WallId != wallId)
            {
                return Results.NotFound();
            }

            var versioned = ImageResponse.IsVersioned(
                http.Request, ResponsiveImage.GalleryVersion(source, id, image.SizeBytes));

            var path = storage.ResolvePhysicalPath(image.StoragePath);
            if (path == null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            // A rendition has to be buffered — it is produced in memory — so it takes the variant
            // path; without a width the file is still streamed, conditional and range handling
            // included, exactly as before.
            if (w is { } uploadedWidth)
            {
                var uploadedKey = ImageResponse.UploadedGalleryKey(
                    id, image.SizeBytes, image.CapturedAt, image.ContentType, image.Sha256);

                return await ImageResponse.VariantAsync(
                    http,
                    ImageResponse.Etag(uploadedKey.Identity, uploadedKey.Version, uploadedWidth),
                    image.ContentType,
                    immutable: versioned,
                    () => variants.GetOrCreateAsync(
                        uploadedKey,
                        uploadedWidth,
                        async () => await File.ReadAllBytesAsync(path, ct),
                        ct));
            }

            // Handed to Results.File as an entity tag rather than run through ImageResponse: the
            // physical-file result already does conditional and range handling, and keeping it means
            // the file is still streamed instead of buffered.
            http.Response.Headers.CacheControl = versioned
                ? ImageResponse.ImmutableCacheControl
                : ImageResponse.MutableCacheControl;

            return Results.File(
                path,
                image.ContentType,
                lastModified: image.CreatedAt,
                entityTag: EntityTagHeaderValue.Parse(
                    ImageResponse.Etag(id, image.SizeBytes, image.CapturedAt.UtcTicks, image.ContentType)),
                enableRangeProcessing: true);
        }

        // The two legacy sources are blobs on the wall/reset rows. Tag first, bytes only on a miss. An
        // archived reset photo is never replaced, but the privacy pass does rewrite it in place, so it
        // too is immutable only under a url that names its current length.
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
            tag.IsArchived && ImageResponse.IsVersioned(http.Request, ResponsiveImage.GalleryVersion(source, id, tag.Length)),
            async () => (await imageService.GetLegacyImageContentAsync(wallId, gallerySource, id, ct))?.Data,
            wallId, source, id);
    }

    /// <summary>
    /// Same gate as the panel photo: a share token grants the anonymous path, a registered kiosk
    /// tablet may view the one wall it is bolted to, otherwise the caller must be signed in and the
    /// wall must survive the membership query filter.
    /// </summary>
    internal static async Task<bool> HasWallAccessAsync(
        Guid wallId,
        string? token,
        IWallService wallService,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IKioskContext kioskContext,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(token))
        {
            var shared = await wallService.GetWallByShareTokenAsync(token);
            return shared != null && shared.Id == wallId;
        }

        User user;
        try
        {
            user = await currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            // Nobody is signed in and no token was offered. That is a spurious 401 for a registered
            // kiosk tablet asking for the gallery thumbnails of the wall it is bolted to — the same
            // resting state the panel-photo route already allows. Grant it viewing of that ONE wall;
            // every other anonymous caller is refused with a 404 that says less than a challenge.
            return KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId);
        }

        // Deliberately not IWallService.GetWallAsync: a gallery page fires one request per
        // thumbnail and that call drags holds/boulders along. The query filter is the whole check.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;

        return await db.Walls.AnyAsync(w => w.Id == wallId, ct);
    }
}
