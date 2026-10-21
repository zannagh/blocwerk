// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Resolution;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Responses of <see cref="AssetEndpoints"/>: <c>private, max-age=31536000, immutable</c> with <c>ETag: "{sha}"</c>
/// (<c>"{sha}-{width}"</c> for a rendition), the content type and the length.
/// </summary>
/// <remarks>
/// Immutable is only ever sent with bytes that were checked against the sha in the URL: a blob is read only while its
/// row still holds that hash and is then hashed again in memory, a buffered file is hashed after it is read, and a
/// streamed splat is hashed before it is streamed (remembered per file version). Bytes that fail the check are a 404
/// without cache headers, never the new bytes under the old sha. A 304, and a HEAD of a buffered asset, are answered
/// from the resolved row, since neither carries a body.
/// </remarks>
public static class AssetResponse
{
    /// <summary>A database blob or a small file, buffered and checked before it is sent.</summary>
    public static async Task<IResult> OriginalAsync(
        HttpContext http, IAssetResolver resolver, ResolvedAsset asset, CancellationToken ct)
    {
        if (Prepare(http, Etag(asset.Hex)) is { } early)
        {
            return early;
        }

        if (HttpMethods.IsHead(http.Request.Method))
        {
            return Head(http, asset.ContentType, asset.Length);
        }

        var bytes = await resolver.LoadAsync(asset, ct);
        return bytes is null ? ImageResponse.NotFound(http) : Results.File(bytes, asset.ContentType);
    }

    /// <summary>A splat: streamed from disk with Range support (a phone can resume), checked first.</summary>
    public static async Task<IResult> StreamAsync(
        HttpContext http, IAssetResolver resolver, ResolvedAsset asset, CancellationToken ct)
    {
        if (Prepare(http, Etag(asset.Hex)) is { } early)
        {
            return early;
        }

        return await resolver.VerifyFileAsync(asset, ct)
            ? Results.File(asset.PhysicalPath!, asset.ContentType, enableRangeProcessing: true)
            : ImageResponse.NotFound(http);
    }

    /// <summary>
    /// A rendition, cached by <c>(sha, width)</c>. Only a rendition of checked bytes is ever cached (the variant cache
    /// re-hashes the original before it writes), so the cached file is as trustworthy as the original.
    /// </summary>
    public static async Task<IResult> VariantAsync(
        HttpContext http,
        IAssetResolver resolver,
        IImageVariantCache variants,
        ResolvedAsset asset,
        int width,
        CancellationToken ct)
    {
        if (Prepare(http, Etag($"{asset.Hex}-{width}")) is { } early)
        {
            return early;
        }

        var variant = await variants.GetOrCreateAsync(
            new ImageVariantKey(string.Empty, string.Empty, asset.Hex), width, () => resolver.LoadAsync(asset, ct), ct);
        if (variant is null)
        {
            return ImageResponse.NotFound(http);
        }

        var type = variant.IsOriginal ? asset.ContentType : variant.ContentType;
        return HttpMethods.IsHead(http.Request.Method)
            ? Head(http, type, variant.Bytes.LongLength)
            : Results.File(variant.Bytes, type);
    }

    private static string Etag(string value) => $"\"{value}\"";

    /// <summary>Writes the validator and the immutable policy; a 304 when the caller already holds these bytes.</summary>
    private static IResult? Prepare(HttpContext http, string etag)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = ImageResponse.ImmutableCacheControl;
        return ImageResponse.Matches(http.Request, etag) ? Results.StatusCode(StatusCodes.Status304NotModified) : null;
    }

    private static IResult Head(HttpContext http, string type, long length)
    {
        http.Response.ContentType = type;
        http.Response.ContentLength = length;
        return Results.Empty;
    }
}
