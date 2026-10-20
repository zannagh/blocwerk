// <copyright file="WallPanelCloseUpEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.HybridZoom;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The hybrid zoom's close-up of a zoomed panel view (<see cref="IHybridZoomService"/>): a JPEG that lies exactly
/// over the panel rectangle given in <c>X-Bw-Closeup-Rect</c> (which may be smaller than asked), 204 with
/// <c>X-Bw-Closeup-Status</c> when no capture photo beats the panel photo there, 503 while too many renders run.
/// Gated like the other wall media (<see cref="WallPanelPhotoEndpoints"/>): a member, a share-token viewer or the
/// wall's kiosk; API-key principals are rejected. 404 whenever the experiment is off.
/// </summary>
public static class WallPanelCloseUpEndpoint
{
    public const string Route = "/api/walls/{wallId:guid}/panels/{panelId:guid}/closeup";

    /// <summary>The panel rectangle the returned image covers, "x,y,w,h" normalised.</summary>
    public const string RectHeader = "X-Bw-Closeup-Rect";

    /// <summary>Why nothing was returned (no-model, no-mapping, no-better-photo, on-volume).</summary>
    public const string StatusHeader = "X-Bw-Closeup-Status";

    /// <summary>The volume a view is on (status on-volume) or that is cut out of the close-up.</summary>
    public const string VolumeHeader = "X-Bw-Closeup-Volume";

    /// <summary>With <see cref="VolumeHeader"/>: the 3D view's <c>?at=</c> spot that looks underneath it (<c>facet:a:b:h:under</c>).</summary>
    public const string SpotHeader = "X-Bw-Closeup-Spot";

    public static void MapWallPanelCloseUp(this WebApplication app)
    {
        app.MapGet(Route, HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
    }

    internal static async Task<IResult> HandleAsync(
        Guid wallId,
        Guid panelId,
        [FromQuery] double x,
        [FromQuery] double y,
        [FromQuery] double w,
        [FromQuery] double h,
        [FromQuery] int? px,
        [FromQuery] double? ar,
        [FromQuery] string? token,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] HybridZoomOptions options,
        [FromServices] IHybridZoomService zoom,
        [FromServices] IWallService wallService,
        [FromServices] ICurrentUserService currentUserService,
        [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        [FromServices] IKioskContext kioskContext,
        CancellationToken ct)
    {
        if (!options.Enabled || user.IsApiKeyPrincipal() || ViewRect.Create(x, y, w, h) is not { } rect)
        {
            return Results.NotFound();
        }

        if (!await WallGalleryImageEndpoint.HasWallAccessAsync(
                wallId, token, wallService, currentUserService, dbContextFactory, kioskContext, ct))
        {
            return Results.NotFound();
        }

        var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
        var result = await zoom.RenderAsync(wallId, panelId, rect, px ?? options.MaxOutputPx, ar, ifNoneMatch, ct);
        return Respond(http, result);
    }

    private static IResult Respond(HttpContext http, CloseUpResult result)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "private, max-age=600";
        if (result.Volume is { } volume)
        {
            headers[VolumeHeader] = volume.Id.ToString();
            headers[SpotHeader] = volume.ToUnderQuery();
        }

        switch (result.Status)
        {
            case CloseUpStatus.NotFound:
                return Results.NotFound();
            case CloseUpStatus.Busy:
                headers.RetryAfter = "2";
                headers.CacheControl = "no-store";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            case CloseUpStatus.Rendered:
                headers.ETag = result.ETag;
                headers[RectHeader] = result.Rect?.ToHeader();
                headers["X-Bw-Closeup-Photo"] = result.PhotoName;
                headers["X-Bw-Closeup-Gain"] = result.Gain?.ToString("0.00", CultureInfo.InvariantCulture);
                if (result.DeltaE is { } de)
                {
                    headers["X-Bw-Closeup-DeltaE"] = string.Create(CultureInfo.InvariantCulture, $"{de.Before:0.00},{de.After:0.00}");
                }

                if (result.CastDeltaE is { } cast)
                {
                    headers["X-Bw-Closeup-Cast-DeltaE"] = string.Create(CultureInfo.InvariantCulture, $"{cast.Before:0.00},{cast.After:0.00}");
                }

                return result.Image is { } image ? Results.Bytes(image, result.ContentType) : Results.StatusCode(StatusCodes.Status304NotModified);
            default:
                headers[StatusHeader] = StatusText(result.Status);
                return Results.NoContent();
        }
    }

    private static string StatusText(CloseUpStatus status) => status switch
    {
        CloseUpStatus.NoModel => "no-model",
        CloseUpStatus.NoMapping => "no-mapping",
        CloseUpStatus.OnVolume => "on-volume",
        CloseUpStatus.StaleCapture => "stale-capture",
        _ => "no-better-photo",
    };
}
