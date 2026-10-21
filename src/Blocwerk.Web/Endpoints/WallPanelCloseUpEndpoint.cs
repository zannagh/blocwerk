// <copyright file="WallPanelCloseUpEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Services.HybridZoom;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The hybrid zoom's close-up of a zoomed panel view (<see cref="IHybridZoomService"/>): a JPEG that lies exactly
/// over the panel rectangle given in <c>X-Bw-Closeup-Rect</c> (which may be smaller than asked), 204 with
/// <c>X-Bw-Closeup-Status</c> when no capture photo beats the panel photo there, 503 while too many renders run.
/// Signed-in wall members only by default (<see cref="CloseUpAccess"/>; share-token viewers and the kiosk only with
/// <see cref="HybridZoomOptions.AllowShareAndKiosk"/>); API-key principals are rejected. 404 whenever the experiment
/// is off — before any query value is read, so a malformed request cannot tell that the route exists.
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

    /// <remarks>The query is read by hand rather than bound, so the flag-off 404 comes before any binding error.</remarks>
    internal static async Task<IResult> HandleAsync(
        Guid wallId,
        Guid panelId,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] HybridZoomOptions options,
        [FromServices] IHybridZoomService zoom,
        [FromServices] ICurrentUserService currentUserService,
        [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        [FromServices] IKioskContext kioskContext,
        CancellationToken ct)
    {
        if (!options.Enabled || user.IsApiKeyPrincipal())
        {
            return Results.NotFound();
        }

        var query = http.Request.Query;
        string? token = query["token"];
        if (!await CloseUpAccess.AllowsAsync(wallId, token, options, currentUserService, dbContextFactory, kioskContext, ct))
        {
            return Results.NotFound();
        }

        if (Number(query["x"]) is not { } x || Number(query["y"]) is not { } y || Number(query["w"]) is not { } w
            || Number(query["h"]) is not { } h || ViewRect.Create(x, y, w, h) is not { } rect)
        {
            return Results.BadRequest();
        }

        var px = Number(query["px"]) is { } p && p is >= 1 and <= 16384 ? (int)p : options.MaxOutputPx;
        var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
        var result = await zoom.RenderAsync(wallId, panelId, rect, px, Number(query["ar"]), ifNoneMatch, ct);
        return Respond(http, result);
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static IResult Respond(HttpContext http, CloseUpResult result)
    {
        var headers = http.Response.Headers;

        // Revalidated on every use under the render's ETag (a 304 costs no render), never kept by max-age.
        headers.CacheControl = ImageResponse.MutableCacheControl;
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
