// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Reads a beta upload's multipart body: exactly one <c>file</c> part streamed to a temp file, an optional
/// <c>thumbnail</c> of at most <see cref="MaxThumbnailBytes"/> and an optional <c>fileName</c> of at most
/// <see cref="MaxFileNameBytes"/>. Only the clip goes to disk; the two small parts are read into memory under their
/// caps, so no part can make the server buffer an unbounded body. Every refusal and every exception deletes the temp
/// file before it returns.
/// </summary>
internal static class BetaVideoMultipart
{
    /// <summary>The largest poster frame accepted: a browser-made JPEG is a few dozen KB.</summary>
    public const int MaxThumbnailBytes = 512 * 1024;

    /// <summary>The largest <c>fileName</c> field accepted.</summary>
    public const int MaxFileNameBytes = 1024;

    public static async Task<BetaVideoParts> ReadAsync(MultipartReader reader, IBetaVideoStorage storage, CancellationToken ct)
    {
        string? tempPath = null;
        string? contentType = null;
        string? fileName = null;
        byte[]? thumbnail = null;
        var handedOver = false;
        try
        {
            for (var section = await reader.ReadNextSectionAsync(ct); section is not null; section = await reader.ReadNextSectionAsync(ct))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                var name = disposition.Name.Value;
                if (name == "file" && (disposition.FileName.HasValue || disposition.FileNameStar.HasValue))
                {
                    if (tempPath is not null)
                    {
                        return BetaVideoParts.Refused("Send one video per upload.");
                    }

                    contentType = string.IsNullOrWhiteSpace(section.ContentType) ? "video/mp4" : section.ContentType;
                    fileName ??= disposition.FileNameStar.Value ?? disposition.FileName.Value;
                    tempPath = storage.CreateTempPath(Path.GetExtension(fileName ?? string.Empty));
                    await using var file = File.Create(tempPath);
                    await section.Body.CopyToAsync(file, ct);
                }
                else if (name is "thumbnail" or "fileName")
                {
                    var (bytes, error) = await ReadSmallPartAsync(section.Body, name, ct);
                    if (error is not null)
                    {
                        return BetaVideoParts.Refused(error);
                    }

                    thumbnail = name == "thumbnail" ? (bytes!.Length > 0 ? bytes : null) : thumbnail;
                    fileName = name == "fileName" ? System.Text.Encoding.UTF8.GetString(bytes!) : fileName;
                }
            }

            if (tempPath is null)
            {
                return BetaVideoParts.Refused("No video file part was sent.");
            }

            handedOver = true;
            return new BetaVideoParts(tempPath, contentType, fileName, thumbnail, null);
        }
        finally
        {
            if (!handedOver)
            {
                Discard(tempPath);
            }
        }
    }

    /// <summary>Deletes a temp file if it is there; never throws.</summary>
    public static void Discard(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The scheduled sweep of the temp folder removes what is left.
        }
    }

    // A thumbnail or fileName part under its cap, or the refusal.
    private static async Task<(byte[]? Bytes, string? Error)> ReadSmallPartAsync(Stream body, string name, CancellationToken ct)
    {
        var cap = name == "thumbnail" ? MaxThumbnailBytes : MaxFileNameBytes;
        return await ReadCappedAsync(body, cap, ct) is { } bytes
            ? (bytes, null)
            : (null, name == "thumbnail" ? "The thumbnail must not exceed 512 KB." : "The file name is too long.");
    }

    // The part's bytes, or null when it is longer than the cap (read no further than cap + 1).
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int cap, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
