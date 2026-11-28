// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The wall gallery's upload pipeline, shared by the camera's wall key (<see cref="WallImagesController"/>) and the
/// companion app (<see cref="DeviceWallGalleryController"/>): read the request to a temp file (multipart or raw, at
/// most <see cref="WallImageUploads.MaxUploadBytes"/>, JPEG, PNG or WebP), strip its metadata, move it into the store and
/// record the row.
/// </summary>
internal static class WallImageIntake
{
    /// <summary>Reads and cleans the image of <paramref name="request"/>; a failure carries the status to answer.</summary>
    public static async Task<WallImageUpload> ReadAsync(HttpRequest request, IWallImageStorage storage, CancellationToken ct)
    {
        // Let the request through up to our own cap plus a little multipart framing slack, so the
        // caller gets our 413 with a readable message instead of Kestrel's bare connection reset.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = WallImageUploads.MaxUploadBytes + (1024 * 1024);
        }

        var isMultipart = request.ContentType?.Contains("multipart/", StringComparison.OrdinalIgnoreCase) == true;
        var upload = isMultipart
            ? await WallImageUploads.ReadMultipartAsync(request, storage, ct)
            : await WallImageUploads.ReadRawAsync(request, storage, ct);

        return upload.IsSuccess ? await WallImageUploads.RemoveMetadataAsync(upload, ct) : upload;
    }

    /// <summary>
    /// Moves the temp file into the store and records the row. Anything that goes wrong after the
    /// commit removes the orphaned file again, so a failed record never leaves bytes behind.
    /// </summary>
    public static async Task<WallImageCommit> CommitAsync(
        IWallImageStorage storage,
        IWallImageService imageService,
        Guid wallId,
        WallImageUpload upload,
        CancellationToken ct)
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
            return WallImageCommit.Failed(StatusCodes.Status500InternalServerError, "The image could not be stored.");
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
                ct);
            return new WallImageCommit(image, null, null);
        }
        catch (InvalidOperationException ex)
        {
            // "Wall not found": the key outlived the wall it was issued for.
            storage.Delete(storedName);
            return WallImageCommit.Failed(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (Exception ex)
        {
            storage.Delete(storedName);
            Log.Error(ex, "Wall image could not be recorded for wall {WallId}", wallId);
            return WallImageCommit.Failed(StatusCodes.Status500InternalServerError, "The image could not be stored.");
        }
    }
}
