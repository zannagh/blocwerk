// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

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
/// The photo-real (Gaussian splat, <c>.spz</c>) scene of a solved glyph wall model, for the 3D view.
/// Same posture as <see cref="WallGeometryTextureEndpoints"/>: a member of the wall, an anonymous
/// viewer holding its share token, or the wall's own kiosk tablet; API-key principals are rejected;
/// a model of another wall is a 404. A splat row is written once and replaced by a NEW row (new id),
/// so it is served immutable under an ETag of its id.
/// </summary>
public static class WallGeometrySplatEndpoints
{
    public const string Route = "/api/walls/{wallId:guid}/geometry/{modelId:guid}/splat";

    /// <summary><c>.spz</c> has no registered media type; it is an opaque gzip-compressed binary.</summary>
    public const string ContentType = "application/octet-stream";

    public static void MapWallGeometrySplats(this WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
    }

    internal static async Task<IResult> HandleAsync(
        Guid wallId,
        Guid modelId,
        [FromQuery] string? token,
        [FromQuery] string? lod,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] IWallService wallService,
        [FromServices] ICurrentUserService currentUserService,
        [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        [FromServices] IKioskContext kioskContext,
        [FromServices] ICaptureFileStore files,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal())
        {
            return Results.NotFound();
        }

        if (!await WallGalleryImageEndpoint.HasWallAccessAsync(
                wallId, token, wallService, currentUserService, dbContextFactory, kioskContext, ct))
        {
            return Results.NotFound();
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var splat = await WallGeometrySplats.FindAsync(db, wallId, modelId, ct);
        if (splat is null)
        {
            return Results.NotFound();
        }

        // Streamed from disk (a splat is megabytes, unlike a texture), with range support so an
        // interrupted download on a phone can resume.
        // ?lod=<splats>: a level of the ladder (SplatLodLadder); ?lod=mobile: the legacy pruned copy of
        // rows that predate it; otherwise (or for a level the row does not have) the full scene.
        var (stored, size, kind) = WallGeometrySplats.Select(splat, lod);
        var path = files.ResolvePhysicalPath(stored);
        if (path is null || !File.Exists(path))
        {
            return Results.NotFound();
        }

        var etag = ImageResponse.Etag(kind, splat.Id, size);
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = ImageResponse.ImmutableCacheControl;
        return ImageResponse.Matches(http.Request, etag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.File(path, ContentType, enableRangeProcessing: true);
    }
}
