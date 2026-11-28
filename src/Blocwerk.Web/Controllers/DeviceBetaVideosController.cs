// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// A boulder's beta clips for the companion app: list, upload, delete. The list is the web carousel's
/// (<see cref="IBetaVideoService.GetVideosAsync"/>), the upload is the browser's streamed multipart upload
/// (<see cref="BetaVideoUploadEndpoint.UploadAsync"/>: members of the wall, a <c>file</c> part with a video type and an
/// optional <c>thumbnail</c> JPEG, normalized in the background) and the delete is the web's (the uploader only). Playback
/// is <see cref="DeviceBetaVideoPlaybackController"/>.
/// </summary>
/// <remarks>
/// A linked device key only, never cached. A wall the owner is not a member of, an unknown boulder and a boulder of
/// another wall answer the same 404. The web upload is one request, not resumable, so the app's is capped at
/// <see cref="MaxUploadBytes"/> (or the server's own cap, if lower) and needs a Content-Length. An upload with a
/// <c>clientRequestId</c> (query string) is done once: a retry returns the stored clip.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}/boulders/{boulderId:guid}/beta-videos")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceBetaVideosController : ControllerBase
{
    /// <summary>The largest clip the app may upload in its single request: 1 GiB.</summary>
    public const long MaxUploadBytes = 1L << 30;

    /// <summary>The receipt kind of a beta upload.</summary>
    public const string ReceiptKind = "beta";

    private readonly IBoulderService boulders;
    private readonly IBetaVideoService videos;
    private readonly ICurrentUserService currentUser;
    private readonly DeviceWriteReceiptService receipts;

    public DeviceBetaVideosController(
        IBoulderService boulders,
        IBetaVideoService videos,
        ICurrentUserService currentUser,
        DeviceWriteReceiptService receipts)
    {
        this.boulders = boulders;
        this.videos = videos;
        this.currentUser = currentUser;
        this.receipts = receipts;
    }

    /// <summary>The boulder's clips, newest first, with their playback URLs.</summary>
    [HttpGet]
    [DeviceKeySyncRateLimit]
    public async Task<IActionResult> List(Guid wallId, Guid boulderId)
    {
        try
        {
            if (!await OnWallAsync(wallId, boulderId))
            {
                return NotFound();
            }

            var me = await currentUser.GetCurrentUserAsync();
            var list = await videos.GetVideosAsync(boulderId);
            return Ok(list.Select(v => ToResponse(v, me.Id)).ToList());
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>Uploads one clip as multipart/form-data, the browser's body.</summary>
    [HttpPost]
    [DeviceKeyWriteRateLimit]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Upload(
        Guid wallId,
        Guid boulderId,
        [FromQuery] Guid? clientRequestId,
        [FromServices] IBetaVideoStorage storage,
        [FromServices] BlocwerkSettings settings,
        [FromServices] ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (clientRequestId == Guid.Empty)
        {
            return OfflineActionRunner.Permanent(ClientRequestIds.EmptyMessage);
        }

        var cap = Math.Min(MaxUploadBytes, settings.BetaVideo.MaxUploadBytes);
        if (SizeRefusal(Request.ContentLength, cap) is { } tooBig)
        {
            return tooBig;
        }

        Guid userId;
        try
        {
            if (!await OnWallAsync(wallId, boulderId))
            {
                return DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Boulder not found");
            }

            userId = (await currentUser.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return DeviceWriteAnswers.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync.");
        }

        var claim = await receipts.ClaimAsync(userId, clientRequestId, ReceiptKind, boulderId, ct);
        if (claim.State == DeviceWriteClaimState.Replay)
        {
            return OfflineActionRunner.Applied(clientRequestId, new { videoId = claim.SubjectId });
        }

        if (DeviceWriteAnswers.Refusal(claim) is { } refused)
        {
            return refused;
        }

        var result = await BetaVideoUploadEndpoint.UploadAsync(boulderId, HttpContext, videos, storage, cap, loggers, ct);
        return await AnswerAsync(result, userId, clientRequestId, ct);
    }

    /// <summary>Deletes one of the caller's own clips, as the web's delete button does.</summary>
    [HttpDelete("{videoId:guid}")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> Delete(Guid wallId, Guid boulderId, Guid videoId, [FromQuery] Guid? clientRequestId)
    {
        try
        {
            if (!await OnWallAsync(wallId, boulderId)
                || !(await videos.GetVideosAsync(boulderId)).Any(v => v.Id == videoId))
            {
                return DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Beta video not found");
            }

            await videos.DeleteVideoAsync(videoId);
            return OfflineActionRunner.Applied(clientRequestId, new { videoId });
        }
        catch (UnauthorizedAccessException)
        {
            return DeviceWriteAnswers.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync.");
        }
        catch (InvalidOperationException)
        {
            // Not the caller's clip, or already gone.
            return DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Beta video not found");
        }
    }

    // A missing Content-Length (411) or one over the cap (413), refused before a byte is read.
    private static IActionResult? SizeRefusal(long? contentLength, long cap) => contentLength switch
    {
        null => DeviceWriteAnswers.Refused(StatusCodes.Status411LengthRequired, "Send the clip with a Content-Length."),
        > 0 and var length when length > cap =>
            DeviceWriteAnswers.Refused(StatusCodes.Status413PayloadTooLarge, $"Clips must not exceed {cap >> 20} MB."),
        _ => null,
    };

    private static DeviceBetaVideoResponse ToResponse(BetaVideoInfo v, Guid me) => new(
        v.Id,
        v.UploadedByUserId,
        v.UploaderName,
        v.CreatedAt,
        v.ContentType,
        v.SizeBytes,
        v.EncodingStatus.ToString(),
        v.ThumbnailSha,
        $"/api/v1/beta-videos/{v.Id}",
        v.HasHls ? $"/api/v1/beta-videos/{v.Id}/hls/master.m3u8" : null,
        v.HasThumbnail ? $"/api/v1/beta-videos/{v.Id}/thumbnail" : null,
        v.UploadedByUserId == me);

    // The web upload's own answers, in the queue's envelope; the receipt is completed or released to match.
    private async Task<IActionResult> AnswerAsync(IResult result, Guid userId, Guid? clientRequestId, CancellationToken ct)
    {
        if (result is Ok<BetaVideoInfo> { Value: { } info })
        {
            await receipts.CompleteAsync(userId, clientRequestId, info.Id, ct);
            return OfflineActionRunner.Applied(clientRequestId, new { videoId = info.Id });
        }

        await receipts.ReleaseAsync(userId, clientRequestId);
        return result switch
        {
            BadRequest<string> bad => DeviceWriteAnswers.Refused(StatusCodes.Status400BadRequest, bad.Value ?? "Bad upload."),
            UnauthorizedHttpResult => DeviceWriteAnswers.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync."),
            ForbidHttpResult => DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Boulder not found"),
            _ => OfflineActionRunner.Error(StatusCodes.Status500InternalServerError, "The upload could not be processed.", permanent: false),
        };
    }

    // The boulder is on this wall and the caller may see it (a member; the web page's own read).
    private async Task<bool> OnWallAsync(Guid wallId, Guid boulderId) =>
        await boulders.GetBoulderAsync(boulderId) is { } boulder && boulder.WallId == wallId;
}
