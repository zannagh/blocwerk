// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Identity-dependent bytes (review crops, close-ups, capture photos, HLS): <c>private, no-cache</c> with an ETag of the
/// exact bytes, never a <c>max-age</c>. The gate then runs on every use, so a revoked viewer or the next identity on a
/// shared device never gets them from the browser cache, and an unchanged picture still costs only a 304.
/// </summary>
public static class RevalidatedResponse
{
    /// <summary>The bytes under an ETag of their content hash; a 304 when the caller holds them already.</summary>
    public static IResult Bytes(HttpContext http, byte[] bytes, string contentType)
    {
        var etag = $"\"{ContentHash.ToHex(ContentHash.Of(bytes))![..32]}\"";
        http.Response.Headers.CacheControl = ImageResponse.MutableCacheControl;
        http.Response.Headers.ETag = etag;
        return ImageResponse.Matches(http.Request, etag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.File(bytes, contentType);
    }

    /// <summary>A file under an ETag of its length and write time; the file result answers <c>If-None-Match</c> itself.</summary>
    public static IResult File(HttpContext http, string path, string contentType)
    {
        var info = new FileInfo(path);
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        http.Response.Headers.CacheControl = ImageResponse.MutableCacheControl;
        return Results.File(path, contentType, lastModified: info.LastWriteTimeUtc, entityTag: etag, enableRangeProcessing: true);
    }
}
