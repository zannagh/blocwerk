// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// A beta clip's playback for the companion app, under the API-key surface: the web's own handlers for the progressive
/// file (<c>/api/beta-videos/{id}</c>), the poster frame and the HLS ladder, with the same gate (a member of the clip's
/// wall; anything else is 404). The ladder's playlists use relative URIs, so a player that sends the key on every
/// request of the asset follows them under this prefix.
/// </summary>
/// <remarks>
/// A linked device key only, counted in the key's sync bucket. Every answer is <c>no-store</c>, overriding the web
/// routes' own cache headers: the gate runs per request and the bytes are one wall's.
/// </remarks>
[ApiController]
[Route("api/v1/beta-videos/{videoId:guid}")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeySyncRateLimit]
public sealed class DeviceBetaVideoPlaybackController : ControllerBase
{
    private readonly IBetaVideoService videos;

    public DeviceBetaVideoPlaybackController(IBetaVideoService videos)
    {
        this.videos = videos;
    }

    /// <summary>The clip's file, with range requests (404 while it is still being normalized).</summary>
    [HttpGet]
    public async Task<IResult> Video(Guid videoId) => NoStore(await BetaVideoFileEndpoints.ServeVideoAsync(videoId, null, videos));

    /// <summary>The clip's poster frame.</summary>
    [HttpGet("thumbnail")]
    public async Task<IResult> Thumbnail(Guid videoId) => NoStore(await BetaVideoFileEndpoints.ServeThumbnailAsync(videoId, null, videos));

    /// <summary>A file of the clip's HLS ladder: <c>master.m3u8</c>, a variant playlist or a segment.</summary>
    [HttpGet("hls/{*path}")]
    public async Task<IResult> Hls(Guid videoId, string path, [FromServices] IBetaVideoStorage storage) =>
        NoStore(await BetaVideoHlsEndpoints.HandleAsync(videoId, path, null, HttpContext, videos, storage));

    // The web handlers set their own Cache-Control while the result runs; this one is applied as the response starts.
    private IResult NoStore(IResult result)
    {
        Response.OnStarting(() =>
        {
            Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return result;
    }
}
