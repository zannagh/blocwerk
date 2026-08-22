using System.Globalization;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Reads an uploaded wall image off the request and onto disk without ever holding it whole in
/// memory, mirroring the beta-video upload. Two transports are supported because the callers are
/// very different: a browser or script posts <c>multipart/form-data</c>, while a Pi with nothing
/// but curl posts the file as the raw request body.
/// </summary>
internal static class WallImageUploads
{
    /// <summary>Hard cap per image. Anything larger is a mis-configured camera, not a photo.</summary>
    public const long MaxUploadBytes = 20L * 1024 * 1024;

    private const string TooLargeMessage = "Images must not exceed 20 MB.";

    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    public static bool IsAllowedContentType(string? contentType)
    {
        return NormalizeContentType(contentType) is not null;
    }

    /// <summary>Strips any parameters ("image/jpeg; charset=…") and matches the allowlist.</summary>
    public static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';');
        var bare = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return AllowedTypes.ContainsKey(bare) ? bare.ToLowerInvariant() : null;
    }

    public static string ExtensionFor(string contentType)
    {
        return AllowedTypes[contentType];
    }

    /// <summary>Reads the raw-body form: the whole request body is the image.</summary>
    public static async Task<WallImageUpload> ReadRawAsync(
        HttpRequest request,
        IWallImageStorage storage,
        CancellationToken cancellationToken)
    {
        var contentType = NormalizeContentType(request.ContentType);
        if (contentType is null)
        {
            return WallImageUpload.Failed(
                StatusCodes.Status415UnsupportedMediaType,
                "Send multipart/form-data or a raw image/jpeg, image/png or image/webp body.");
        }

        var caption = request.Query["caption"].FirstOrDefault();
        var capturedAt = ParseCapturedAt(request.Query["capturedAt"].FirstOrDefault());

        var tempPath = storage.CreateTempPath(ExtensionFor(contentType));
        var size = await CopyLimitedAsync(request.Body, tempPath, cancellationToken);
        if (size is null)
        {
            Discard(tempPath);
            return WallImageUpload.Failed(StatusCodes.Status413PayloadTooLarge, TooLargeMessage);
        }

        if (size == 0)
        {
            Discard(tempPath);
            return WallImageUpload.Failed(StatusCodes.Status400BadRequest, "The image body was empty.");
        }

        return WallImageUpload.Succeeded(tempPath, contentType, ExtensionFor(contentType), size.Value, caption, capturedAt);
    }

    /// <summary>Reads the multipart form: a <c>file</c> part plus optional caption/capturedAt fields.</summary>
    public static async Task<WallImageUpload> ReadMultipartAsync(
        HttpRequest request,
        IWallImageStorage storage,
        CancellationToken cancellationToken)
    {
        var boundary = HeaderUtilities.RemoveQuotes(
            MediaTypeHeaderValue.Parse(request.ContentType!).Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return WallImageUpload.Failed(StatusCodes.Status400BadRequest, "Malformed multipart upload.");
        }

        var reader = new MultipartReader(boundary, request.Body);
        string? tempPath = null;
        var handedOver = false;
        string? contentType = null;
        string? caption = request.Query["caption"].FirstOrDefault();
        var capturedAt = ParseCapturedAt(request.Query["capturedAt"].FirstOrDefault());

        try
        {
            for (var section = await reader.ReadNextSectionAsync(cancellationToken);
                 section is not null;
                 section = await reader.ReadNextSectionAsync(cancellationToken))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                var name = disposition.Name.Value;
                if (name == "file")
                {
                    contentType = NormalizeContentType(section.ContentType);
                    if (contentType is null)
                    {
                        return WallImageUpload.Failed(
                            StatusCodes.Status415UnsupportedMediaType,
                            "Only image/jpeg, image/png and image/webp are accepted.");
                    }

                    tempPath = storage.CreateTempPath(ExtensionFor(contentType));
                    var size = await CopyLimitedAsync(section.Body, tempPath, cancellationToken);
                    if (size is null)
                    {
                        return WallImageUpload.Failed(StatusCodes.Status413PayloadTooLarge, TooLargeMessage);
                    }

                    if (size == 0)
                    {
                        return WallImageUpload.Failed(StatusCodes.Status400BadRequest, "The image part was empty.");
                    }

                    handedOver = true;
                    return WallImageUpload.Succeeded(
                        tempPath,
                        contentType,
                        ExtensionFor(contentType),
                        size.Value,
                        caption,
                        capturedAt);
                }

                if (name == "caption")
                {
                    using var sr = new StreamReader(section.Body);
                    caption = await sr.ReadToEndAsync(cancellationToken);
                }
                else if (name == "capturedAt")
                {
                    using var sr = new StreamReader(section.Body);
                    capturedAt = ParseCapturedAt(await sr.ReadToEndAsync(cancellationToken));
                }
            }

            return WallImageUpload.Failed(StatusCodes.Status400BadRequest, "No 'file' part was sent.");
        }
        finally
        {
            // Every failure path drops the half-written temp file; only the success path hands
            // ownership of it to the caller.
            if (!handedOver)
            {
                Discard(tempPath);
            }
        }
    }

    public static void Discard(string? tempPath)
    {
        if (tempPath is not null && File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Streams to <paramref name="tempPath"/> and returns the byte count, or null once the cap is
    /// exceeded. The cap is enforced while copying rather than from Content-Length, because a
    /// chunked upload does not send one.
    /// </summary>
    private static async Task<long?> CopyLimitedAsync(
        Stream source,
        string tempPath,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        await using var target = File.Create(tempPath);
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return total;
            }

            total += read;
            if (total > MaxUploadBytes)
            {
                return null;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static DateTimeOffset? ParseCapturedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
