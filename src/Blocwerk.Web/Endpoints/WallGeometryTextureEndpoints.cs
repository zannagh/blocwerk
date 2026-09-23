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
/// it is served immutable under an ETag of its id.
/// </summary>
public static class WallGeometryTextureEndpoints
{
    public const string Route = "/api/walls/{wallId:guid}/geometry/{modelId:guid}/textures/{facetId}";

    public static void MapWallGeometryTextures(this WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
    }

    internal static async Task<IResult> HandleAsync(
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

        return await ImageResponse.ConditionalAsync(
            http,
            ImageResponse.Etag("geometry-texture", texture.Id, texture.SizeBytes),
            texture.ContentType,
            texture.SizeBytes,
            immutable: true,
            () => files.ReadAsync(texture.StoredPath, ct));
    }
}
