// <copyright file="WallCapturesController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Capture;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The in-app capture over the machine API, for automation ("drop the photos, the wall gets better 3D data"):
/// open a draft, stream photos into it, optionally attach a marker plan, read the suggested declarations, start it
/// and poll its status — the same <see cref="IWallCaptureService"/> calls the capture panel makes, so the rules
/// are identical. The walk-along video keeps its own streamed route (<c>POST /api/captures/{id}/video</c>).
/// </summary>
/// <remarks>
/// Authorised like <see cref="WallGeometryPlacementController"/>: a wall key for the wall in the route or a personal
/// key with write access, whose OWNER must be an admin of the wall (the service's check); kiosk sessions are
/// refused by the service. A capture id is only served under the wall it belongs to.
/// </remarks>
[ApiController]
[Route("api/walls/{wallId:guid}/captures")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed partial class WallCapturesController(
    IWallCaptureService captures,
    WallCapturePipelineOptions options,
    ILogger<WallCapturesController> logger) : WallAdminApiController(logger)
{
    /// <summary>The wall's captures, newest first (drafts excluded), each with what it did for the wall.</summary>
    [HttpGet]
    public Task<IActionResult> List(Guid wallId) =>
        RunAsync(wallId, async () => Ok(await captures.GetCapturesAsync(wallId)));

    /// <summary>Opens a draft, or returns the key owner's open draft of this wall.</summary>
    [HttpPost("draft")]
    public Task<IActionResult> CreateDraft(Guid wallId) =>
        RunAsync(wallId, async () => Ok(await captures.CreateDraftAsync(wallId)));

    /// <summary>The key owner's open draft of this wall (with its photos), or 404.</summary>
    [HttpGet("draft")]
    public Task<IActionResult> GetDraft(Guid wallId) =>
        RunAsync(wallId, async () => await captures.GetDraftAsync(wallId) is { } draft
            ? Ok(draft)
            : NotFound(new ApiErrorResponse("There is no open capture draft on this wall.")));

    /// <summary>One capture: status, progress, stage, error and the follow-up summary. Poll this after starting.</summary>
    [HttpGet("{captureId:guid}")]
    public Task<IActionResult> Get(Guid wallId, Guid captureId) =>
        ForCaptureAsync(wallId, captureId, async () => Ok(await captures.GetCaptureAsync(captureId)));

    /// <summary>Deletes an open draft and its files.</summary>
    [HttpDelete("{captureId:guid}")]
    public Task<IActionResult> Discard(Guid wallId, Guid captureId) =>
        ForCaptureAsync(wallId, captureId, async () =>
        {
            await captures.DiscardDraftAsync(captureId);
            return NoContent();
        });

    /// <summary>
    /// Streams photos into the draft: a multipart/form-data body with any number of file parts (any field name),
    /// read one part at a time. Each photo is stored and analysed as in the upload panel; a refused photo (not an
    /// image, too big, a duplicate, the limit reached) is reported in its item and the next one still goes in.
    /// </summary>
    [HttpPost("{captureId:guid}/photos")]
    public Task<IActionResult> UploadPhotos(Guid wallId, Guid captureId, CancellationToken ct) =>
        ForCaptureAsync(wallId, captureId, async () =>
        {
            if (!CapturePhotoMultipart.IsMultipart(Request))
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, new ApiErrorResponse("Send the photos as multipart/form-data."));
            }

            AllowPhotoBatchBody();
            var items = new List<CapturePhotoUploadItem>();
            await foreach (var (name, bytes) in CapturePhotoMultipart.ReadFilesAsync(Request, options.MaxPhotoBytes, ct))
            {
                items.Add(await AddPhotoAsync(captureId, name, bytes, ct));
            }

            var stored = items.Count(i => i.Photo is not null);
            return Ok(new CapturePhotoUploadResponse(stored, items.Count - stored, items));
        });
}
