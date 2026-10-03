// <copyright file="CapturePhotoMultipart.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Reads a multipart/form-data body one file part at a time, straight off the request stream: never the whole
/// request in memory (no form buffering), at most one photo (≤ the photo limit) at a time. Parts that are not
/// files are skipped; a part over the limit is reported as such and the rest of it is skipped unread.
/// </summary>
public static class CapturePhotoMultipart
{
    /// <summary>True when the request is multipart/form-data with a boundary.</summary>
    public static bool IsMultipart(HttpRequest request) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
        && type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(HeaderUtilities.RemoveQuotes(type.Boundary).Value);

    /// <summary>The file parts, in order.</summary>
    /// <param name="request">A multipart/form-data request (see <see cref="IsMultipart"/>).</param>
    /// <param name="maxBytes">Largest photo accepted.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Per file part: its name and bytes (null when it was over <paramref name="maxBytes"/>).</returns>
    public static async IAsyncEnumerable<(string? FileName, byte[]? Bytes)> ReadFilesAsync(
        HttpRequest request, long maxBytes, [EnumeratorCancellation] CancellationToken ct)
    {
        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(request.ContentType).Boundary).Value!;
        var reader = new MultipartReader(boundary, request.Body);
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.IsFileDisposition())
            {
                continue;
            }

            var name = disposition.FileNameStar.HasValue
                ? disposition.FileNameStar.Value
                : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
            yield return (name, await ReadBoundedAsync(section.Body, maxBytes, ct));
        }
    }

    /// <summary>The part's bytes, or null as soon as it grows past <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
