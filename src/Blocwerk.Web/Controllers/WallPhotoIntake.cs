// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Microsoft.AspNetCore.Http.Features;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Reads the companion app's wall photo (the raw request body) with the gallery's hardening: at most
/// <see cref="MaxBytes"/>, JPEG, PNG or HEIC only, the bytes must be the declared type, a HEIC is converted to JPEG with
/// its metadata dropped, and the picture must stay under the pixel limit (<see cref="GalleryImageCheck"/>). Location and
/// the other metadata of a JPEG or PNG are stripped by the service's own upload.
/// </summary>
internal static class WallPhotoIntake
{
    /// <summary>The largest photo accepted: the gallery's cap.</summary>
    public const long MaxBytes = WallImageUploads.MaxUploadBytes;

    /// <summary>The photo's bytes and stored type, or the status and message to refuse it with.</summary>
    public static async Task<(byte[]? Bytes, string? ContentType, int Status, string? Error)> ReadAsync(
        HttpRequest request,
        ICapturePhotoConverter? converter,
        CancellationToken ct)
    {
        if (request.ContentLength > MaxBytes)
        {
            return (null, null, StatusCodes.Status413PayloadTooLarge, "Photos must not exceed 20 MB.");
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = MaxBytes + 1;
        }

        var bytes = await ReadCappedAsync(request.Body, ct);
        if (bytes is null)
        {
            return (null, null, StatusCodes.Status413PayloadTooLarge, "Photos must not exceed 20 MB.");
        }

        var declared = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        var kind = CapturePhotoFormat.Sniff(bytes);
        if (bytes.Length == 0 || !Matches(kind, declared))
        {
            return (null, null, StatusCodes.Status415UnsupportedMediaType, "Send the photo as image/jpeg, image/png or image/heic, matching its bytes.");
        }

        if (kind == CapturePhotoKind.Heic)
        {
            bytes = await ToJpegAsync(bytes, converter, ct);
            if (bytes is null)
            {
                return (null, null, StatusCodes.Status415UnsupportedMediaType, "The HEIC photo could not be converted. Send it as JPEG.");
            }
        }

        var type = kind == CapturePhotoKind.Png ? "image/png" : "image/jpeg";
        return GalleryImageCheck.Refusal(bytes, type) is { } refusal
            ? (null, null, refusal.Status, refusal.Message)
            : (bytes, type, StatusCodes.Status200OK, null);
    }

    private static bool Matches(CapturePhotoKind kind, string? declared) => kind switch
    {
        CapturePhotoKind.Jpeg => declared == "image/jpeg",
        CapturePhotoKind.Png => declared == "image/png",
        CapturePhotoKind.Heic => declared is "image/heic" or "image/heif",
        _ => false,
    };

    // A HEIC turned into an upright JPEG with no metadata at all (an orientation tag would turn it a second time).
    private static async Task<byte[]?> ToJpegAsync(byte[] heic, ICapturePhotoConverter? converter, CancellationToken ct)
    {
        if (converter is null)
        {
            return null;
        }

        try
        {
            var jpeg = await converter.ToJpegAsync(heic, ct);
            return CapturePhotoFormat.Sniff(jpeg) == CapturePhotoKind.Jpeg ? ImageMetadataStripper.Strip(jpeg, keepColour: true) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
