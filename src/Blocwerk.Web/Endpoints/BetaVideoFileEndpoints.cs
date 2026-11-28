// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// A beta clip's progressive bytes and its poster frame (<c>/api/beta-videos/{id}</c>, <c>/thumbnail</c>). Access mirrors
/// the wall photo routes: a share token takes the anonymous path, otherwise the caller must be a member of the clip's
/// wall. The companion app reaches the same handlers under <c>/api/v1/beta-videos</c>.
/// </summary>
public static class BetaVideoFileEndpoints
{
    public static void MapBetaVideoFiles(this WebApplication app)
    {
        app.MapGet("/api/beta-videos/{videoId:guid}", ServeVideoAsync);
        app.MapGet("/api/beta-videos/{videoId:guid}/thumbnail", ServeThumbnailAsync);
    }

    /// <summary>
    /// The clip's bytes, with range processing: without it a video element cannot seek, and Safari refuses to play at
    /// all because it opens every media element with a Range request.
    /// </summary>
    internal static async Task<IResult> ServeVideoAsync(
        Guid videoId,
        [FromQuery] string? token,
        [FromServices] IBetaVideoService betaVideoService)
    {
        try
        {
            var video = await betaVideoService.GetVideoFileAsync(videoId, token);
            if (video is null)
            {
                return Results.NotFound();
            }

            // Withhold a clip that has not been verified web-safe yet: Pending is a brand-new,
            // un-normalized upload and Processing may be mid-swap. Ready serves the normalized
            // rendition; Failed serves the ORIGINAL as a fallback so a clip that played before is
            // never made unplayable (the player renders it with a "couldn't optimize" note).
            if (video.EncodingStatus is BetaVideoEncodingStatus.Pending or BetaVideoEncodingStatus.Processing)
            {
                return Results.NotFound();
            }

            // Stream disk-backed clips straight from the file (never load them whole into memory);
            // fall back to the legacy in-database bytes.
            return video.PhysicalPath is not null
                ? Results.File(video.PhysicalPath, video.ContentType, enableRangeProcessing: true)
                : Results.File(video.Bytes!, video.ContentType, enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    /// <summary>The clip's poster frame, or 404 when it has none.</summary>
    internal static async Task<IResult> ServeThumbnailAsync(
        Guid videoId,
        [FromQuery] string? token,
        [FromServices] IBetaVideoService betaVideoService)
    {
        try
        {
            var thumbnail = await betaVideoService.GetThumbnailAsync(videoId, token);
            return thumbnail == null
                ? Results.NotFound()
                : Results.File(thumbnail.Data, thumbnail.ContentType);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }
}
