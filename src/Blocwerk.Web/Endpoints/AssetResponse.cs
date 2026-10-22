// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Responses of <see cref="AssetEndpoints"/>: <c>private, no-cache</c> with <c>ETag: "{sha}"</c>
/// (<c>"{sha}-{width}"</c> for a rendition), the content type and the length.
/// </summary>
/// <remarks>
/// <para>
/// Not <c>immutable</c>, although the bytes are: the browser's HTTP cache is one per browser, not one per identity, and
/// WebKit ignores <c>Clear-Site-Data: "cache"</c> even on the signed-out 200 page, so an immutable copy would outlive a
/// sign-out and be handed to the next identity without a request. With <c>no-cache</c> every reuse revalidates, and the
/// 304 is answered only after the endpoint's access gate. The cache-first copy lives in the service worker's per-identity
/// Cache Storage instead (sw-asset-cache.js), which stores any 200 without <c>no-store</c>.
/// </para>
/// The ETag is only ever sent with bytes that were checked against the sha in the URL: a blob is read only while its
/// row still holds that hash and is then hashed again in memory, a buffered file is hashed after it is read, and a
/// streamed splat is hashed before it is streamed (remembered per file version). Bytes that fail the check are a 404
/// without cache headers, never the new bytes under the old sha. A 304, and a HEAD of a buffered asset, are answered
/// from the resolved row, since neither carries a body; a HEAD of a rendition from the variant cache's file system.
/// </remarks>
public static class AssetResponse
{
    /// <summary>The policy of an asset a kiosk session may see but its shared caches may not keep.</summary>
    public const string NoStoreCacheControl = "no-store";

    /// <summary>Every other asset: kept by the HTTP cache, but revalidated (through the access gate) on every reuse.</summary>
    public const string RevalidateCacheControl = ImageResponse.MutableCacheControl;

    /// <summary>A database blob or a small file, buffered and checked before it is sent.</summary>
    public static async Task<IResult> OriginalAsync(
        HttpContext http, IAssetResolver resolver, ResolvedAsset asset, CancellationToken ct)
    {
        if (Prepare(http, asset, Etag(asset.Hex)) is { } early)
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
        if (Prepare(http, asset, Etag(asset.Hex)) is { } early)
        {
            return early;
        }

        return await resolver.VerifyFileAsync(asset, ct)
            ? Results.File(asset.PhysicalPath!, asset.ContentType, enableRangeProcessing: true)
            : ImageResponse.NotFound(http);
    }

    /// <summary>
    /// A rendition, cached by <c>(sha, width)</c>. Only a rendition of checked bytes is ever cached: the resolver's load
    /// returns only bytes that hash to the sha (through its memo), and the cache writes or adopts nothing else. The key
    /// carries the row's legacy keys (<see cref="AssetLegacyVariantKeys"/>), so a rendition the legacy routes already
    /// made is moved into the sha slot rather than rendered again.
    /// </summary>
    public static async Task<IResult> VariantAsync(
        HttpContext http,
        IAssetResolver resolver,
        IImageVariantCache variants,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ResolvedAsset asset,
        int width,
        CancellationToken ct)
    {
        if (Prepare(http, asset, Etag($"{asset.Hex}-{width}")) is { } early)
        {
            return early;
        }

        if (HttpMethods.IsHead(http.Request.Method))
        {
            var shaKey = new ImageVariantKey(string.Empty, string.Empty, asset.Hex);
            return await HeadOfVariantAsync(http, resolver, asset, variants.PeekHashed(shaKey, width), ct);
        }

        var key = await AssetLegacyVariantKeys.ForAsync(dbContextFactory, asset, ct);
        var variant = await variants.GetOrCreateAsync(key, width, () => resolver.LoadAsync(asset, ct), ct, originalVerified: true);
        if (variant is null)
        {
            return ImageResponse.NotFound(http);
        }

        return Results.File(variant.Bytes, variant.IsOriginal ? asset.ContentType : variant.ContentType);
    }

    /// <summary>
    /// A HEAD of a rendition, never rendered: rendering one (a few hundred milliseconds of CPU) to report a length would
    /// let a HEAD cost what a GET does. A cached rendition answers with its file's length and type. For one not cached
    /// yet the original is loaded and checked as the GET would check it (cheap once the hash memo has it), so a HEAD
    /// never claims a 200 the GET would answer with a 404; it then carries the validator and policy but no type and no
    /// length, which a HEAD may leave out (a cache freshening a stored GET from it keeps the stored ones).
    /// </summary>
    private static async Task<IResult> HeadOfVariantAsync(
        HttpContext http, IAssetResolver resolver, ResolvedAsset asset, (long Length, string ContentType)? cached, CancellationToken ct)
    {
        if (cached is { } file)
        {
            return Head(http, file.ContentType, file.Length);
        }

        return await resolver.LoadAsync(asset, ct) is null ? ImageResponse.NotFound(http) : Results.Empty;
    }

    private static string Etag(string value) => $"\"{value}\"";

    /// <summary>
    /// Writes the validator and the revalidate policy; a 304 when the caller already holds these bytes. A
    /// <see cref="ResolvedAsset.NoStore"/> asset gets <c>no-store</c> instead, with no validator (so no 304 either) and
    /// <c>Vary: Cookie</c>: nothing on the shared tablet, neither the service worker (which skips no-store) nor the HTTP
    /// cache, may keep it for the next identity.
    /// </summary>
    private static IResult? Prepare(HttpContext http, ResolvedAsset asset, string etag)
    {
        if (asset.NoStore)
        {
            http.Response.Headers.CacheControl = NoStoreCacheControl;
            http.Response.Headers.Vary = "Cookie";
            return null;
        }

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = RevalidateCacheControl;
        return ImageResponse.Matches(http.Request, etag) ? Results.StatusCode(StatusCodes.Status304NotModified) : null;
    }

    private static IResult Head(HttpContext http, string type, long length)
    {
        http.Response.ContentType = type;
        http.Response.ContentLength = length;
        return Results.Empty;
    }
}
