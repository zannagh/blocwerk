using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Image gallery of a wall for machine callers: a camera pushes photos in, anything holding the
/// same wall key reads them back. Authentication is API key only.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/images")]
[Authorize(Policy = BlocwerkPolicies.WallApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallImagesController : WallScopedApiController
{
    private const int MaxTake = 200;

    private readonly IWallImageService imageService;
    private readonly IWallImageStorage storage;
    private readonly ICurrentUserService currentUserService;

    public WallImagesController(
        IWallImageService imageService,
        IWallImageStorage storage,
        ICurrentUserService currentUserService)
    {
        this.imageService = imageService;
        this.storage = storage;
        this.currentUserService = currentUserService;
    }

    /// <summary>
    /// Uploads one image. Accepts either a <c>multipart/form-data</c> body with a <c>file</c> part
    /// (plus optional <c>caption</c>/<c>capturedAt</c> fields) or the raw image as the request
    /// body with an <c>image/*</c> content type, in which case caption and capturedAt come from
    /// the query string — that raw form is what a Pi can send with a single curl call.
    /// </summary>
    /// <remarks>
    /// Antiforgery is not applicable: the caller is a machine holding a bearer key, never a
    /// browser form, and no cookie is involved. <c>[IgnoreAntiforgeryToken]</c> is the MVC
    /// spelling of the minimal-API <c>DisableAntiforgery()</c>.
    /// </remarks>
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Upload(Guid wallId, CancellationToken cancellationToken)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        // Let the request through up to our own cap plus a little multipart framing slack, so the
        // caller gets our 413 with a readable message instead of Kestrel's bare connection reset.
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = WallImageUploads.MaxUploadBytes + (1024 * 1024);
        }

        var isMultipart = Request.ContentType?.Contains("multipart/", StringComparison.OrdinalIgnoreCase) == true;
        var upload = isMultipart
            ? await WallImageUploads.ReadMultipartAsync(Request, storage, cancellationToken)
            : await WallImageUploads.ReadRawAsync(Request, storage, cancellationToken);

        if (!upload.IsSuccess)
        {
            return StatusCode(upload.ErrorStatus!.Value, new ApiErrorResponse(upload.ErrorMessage!));
        }

        return await CommitAsync(wallId, upload, cancellationToken);
    }

    /// <summary>The wall's gallery, newest capture first, merged across the three image sources.</summary>
    [HttpGet]
    public async Task<IActionResult> GetGallery(
        Guid wallId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        var items = await imageService.GetGalleryAsync(
            wallId,
            Math.Max(0, skip),
            Math.Clamp(take, 1, MaxTake),
            cancellationToken);

        return Ok(items.Select(i => i.ToResponse()).ToList());
    }

    /// <summary>
    /// Raw bytes of one gallery entry. Uploads are served from the file store; the wall's own
    /// photo and the photos retired by resets still live in the database and come back through the
    /// legacy projection.
    /// </summary>
    [HttpGet("{source}/{id:guid}/content")]
    public async Task<IActionResult> GetContent(
        Guid wallId,
        string source,
        Guid id,
        CancellationToken cancellationToken)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        if (!Enum.TryParse<WallGallerySource>(source, ignoreCase: true, out var parsedSource))
        {
            return NotFound(new ApiErrorResponse("Unknown image source."));
        }

        if (parsedSource != WallGallerySource.Uploaded)
        {
            var legacy = await imageService.GetLegacyImageContentAsync(wallId, parsedSource, id, cancellationToken);
            if (legacy is null)
            {
                return NotFound(new ApiErrorResponse("Image not found."));
            }

            return File(legacy.Data, legacy.ContentType);
        }

        var image = await imageService.GetImageAsync(id, cancellationToken);
        if (image is null || image.WallId != wallId)
        {
            return NotFound(new ApiErrorResponse("Image not found."));
        }

        var path = storage.ResolvePhysicalPath(image.StoragePath);
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound(new ApiErrorResponse("Image not found."));
        }

        return PhysicalFile(path, image.ContentType);
    }

    /// <summary>
    /// Deletes an uploaded image. Only uploads can be deleted — the wall photo and the reset
    /// photos are owned by their own rows. The acting user is the key's owner, which is who the
    /// principal already resolves to.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid wallId, Guid id, CancellationToken cancellationToken)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        var image = await imageService.GetImageAsync(id, cancellationToken);
        if (image is null || image.WallId != wallId)
        {
            return NotFound(new ApiErrorResponse("Image not found."));
        }

        // Resolution can refuse a caller the guard above let through — a key whose owner has since
        // been deleted or merged away. That is a 401, not a 500.
        Core.Entities.User actingUser;
        try
        {
            actingUser = await currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new ApiErrorResponse("The key's owner no longer exists."));
        }

        try
        {
            await imageService.DeleteImageAsync(id, actingUser.Id, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new ApiErrorResponse("The key's owner does not administer this wall."));
        }

        return NoContent();
    }

    /// <summary>
    /// Moves the temp file into the store and records the row. Anything that goes wrong after the
    /// commit removes the orphaned file again, so a failed record never leaves bytes behind.
    /// </summary>
    private async Task<IActionResult> CommitAsync(
        Guid wallId,
        WallImageUpload upload,
        CancellationToken cancellationToken)
    {
        string storedName;
        try
        {
            storedName = storage.Commit(upload.TempPath, upload.Extension);
        }
        catch (Exception ex)
        {
            WallImageUploads.Discard(upload.TempPath);
            Log.Error(ex, "Wall image upload could not be stored for wall {WallId}", wallId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiErrorResponse("The image could not be stored."));
        }

        try
        {
            var image = await imageService.RecordImageAsync(
                wallId,
                storedName,
                upload.ContentType,
                upload.SizeBytes,
                upload.Caption,
                upload.CapturedAt,
                cancellationToken);

            return StatusCode(
                StatusCodes.Status201Created,
                new WallImageCreatedResponse(image.Id, image.CapturedAt));
        }
        catch (InvalidOperationException ex)
        {
            // "Wall not found" — the key outlived the wall it was issued for.
            storage.Delete(storedName);
            return NotFound(new ApiErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            storage.Delete(storedName);
            Log.Error(ex, "Wall image could not be recorded for wall {WallId}", wallId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiErrorResponse("The image could not be stored."));
        }
    }
}
