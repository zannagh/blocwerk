using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The per-facet textures of a solved glyph wall model, for the 3D view. Gated exactly like the
/// other wall media (<see cref="WallPanelPhotoEndpoints"/>, <see cref="WallGalleryImageEndpoint"/>):
/// a member of the wall, an anonymous viewer holding its share token, or the wall's own kiosk
/// tablet; API-key principals are rejected. A texture row is written once and never rewritten, so
/// it is served immutable under an ETag of its id. Its coverage mask (<see cref="MaskRoute"/>) is
/// served by the same gate from the same row.
/// </summary>
public static class WallGeometryTextureEndpoints
{
    public const string Route = "/api/walls/{wallId:guid}/geometry/{modelId:guid}/textures/{facetId}";

    /// <summary>The texture's coverage mask: same gate, same row, its own file.</summary>
    public const string MaskRoute = Route + "/mask";

    public static void MapWallGeometryTextures(this WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
        app.MapMethods(MaskRoute, [HttpMethods.Get, HttpMethods.Head], HandleMaskAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
    }

    internal static Task<IResult> HandleAsync(
        Guid wallId,
        Guid modelId,
        string facetId,
        [FromQuery] string? token,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] IWallService wallService,
        [FromServices] ICurrentUserService currentUserService,
        [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        [FromServices] IKioskContext kioskContext,
        [FromServices] ICaptureFileStore files,
        CancellationToken ct) =>
        ServeAsync(
            wallId, modelId, facetId, token, user, http, false, wallService, currentUserService, dbContextFactory, kioskContext, files, ct);

    /// <summary>The texture's photo-coverage mask (8-bit PNG, its alpha); 404 when it has none.</summary>
    internal static Task<IResult> HandleMaskAsync(
        Guid wallId,
        Guid modelId,
        string facetId,
        [FromQuery] string? token,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] IWallService wallService,
        [FromServices] ICurrentUserService currentUserService,
        [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        [FromServices] IKioskContext kioskContext,
        [FromServices] ICaptureFileStore files,
        CancellationToken ct) =>
        ServeAsync(
            wallId, modelId, facetId, token, user, http, true, wallService, currentUserService, dbContextFactory, kioskContext, files, ct);

    private static async Task<IResult> ServeAsync(
        Guid wallId,
        Guid modelId,
        string facetId,
        string? token,
        ClaimsPrincipal user,
        HttpContext http,
        bool mask,
        IWallService wallService,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IKioskContext kioskContext,
        ICaptureFileStore files,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal() || facetId.Length > 32)
        {
            return Results.NotFound();
        }

        if (!await WallGalleryImageEndpoint.HasWallAccessAsync(
                wallId, token, wallService, currentUserService, dbContextFactory, kioskContext, ct))
        {
            return Results.NotFound();
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var texture = await WallCaptureService.FindTextureAsync(db, wallId, modelId, facetId, ct);
        if (texture is null)
        {
            return Results.NotFound();
        }

        if (mask)
        {
            if (texture.MaskStoredPath is not { } maskPath)
            {
                return Results.NotFound();
            }

            var maskSize = texture.MaskSizeBytes ?? 0;
            return await ImageResponse.ConditionalAsync(
                http,
                ImageResponse.Etag("geometry-texture-mask", texture.Id, maskSize),
                "image/png",
                maskSize,
                immutable: true,
                () => files.ReadAsync(maskPath, ct));
        }

        return await ImageResponse.ConditionalAsync(
            http,
            ImageResponse.Etag("geometry-texture", texture.Id, texture.SizeBytes),
            texture.ContentType,
            texture.SizeBytes,
            immutable: true,
            () => files.ReadAsync(texture.StoredPath, ct));
    }
}
