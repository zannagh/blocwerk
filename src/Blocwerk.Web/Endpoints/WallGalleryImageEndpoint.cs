using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Serves the bytes of one gallery item to the signed-in browser (or an anonymous share-token
/// viewer), mirroring the wall-photo routes: the three gallery sources live in different stores,
/// so uploads stream from the file store and the legacy photos come from their rows.
/// This is the cookie-authenticated UI route; machine callers use the API-key routes under /api.
/// </summary>
public static class WallGalleryImageEndpoint
{
    public static void MapWallGalleryImages(this WebApplication app)
    {
        // The policy admits a signed-in human or an anonymous share-token viewer, and rejects an
        // API-key principal outright: machine callers read gallery bytes through
        // /api/walls/{wallId}/images/{source}/{id}/content, which checks the wall against the
        // key's own wall claim instead of against everything the key's owner may see.
        app.MapGet("/walls/{wallId:guid}/gallery/{source}/{id:guid}", HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage);
    }

    private static async Task<IResult> HandleAsync(
        Guid wallId,
        string source,
        Guid id,
        [FromQuery] string? token,
        ClaimsPrincipal user,
        IWallService wallService,
        IWallImageService imageService,
        IWallImageStorage storage,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal())
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
            var image = await imageService.GetImageAsync(id, ct);
            if (image == null || image.WallId != wallId)
            {
                return Results.NotFound();
            }

            var path = storage.ResolvePhysicalPath(image.StoragePath);
            return path == null || !File.Exists(path)
                ? Results.NotFound()
                : Results.File(path, image.ContentType);
        }

        var content = await imageService.GetLegacyImageContentAsync(wallId, gallerySource, id, ct);
        return content == null ? Results.NotFound() : Results.File(content.Data, content.ContentType);
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
