using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Services;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Conditional-request plumbing shared by the image byte routes. Every image in this app is a
/// Postgres blob served through the app, and the routes used to emit no validator at all, so a
/// browser refetched the whole wall on every navigation — roughly 15 MB for a five-panel wall.
/// Each route now builds an ETag from small metadata columns, and this helper answers a matching
/// <c>If-None-Match</c> with 304 (and a HEAD out of the same metadata) BEFORE the blob is read.
/// </summary>
public static class ImageResponse
{
    /// <summary>
    /// A mutable image: the live wall photo, a staged photo, a panel photo, an avatar.
    /// <c>private</c> because every one of these is authenticated content and must never be held by
    /// a shared proxy. <c>no-cache</c> rather than a <c>max-age</c> because these bytes are replaced
    /// in place by a wall update or an avatar change: no-cache still stores the image in the
    /// browser's own cache and still permits the 304 that saves the megabytes, it just refuses to
    /// serve it without asking first, so an updated wall is never shown stale.
    /// </summary>
    public const string MutableCacheControl = "private, no-cache";

    // No policy here lets the HTTP cache answer without the server, not even for a URL that names its bytes (`?v=`)
    // or a sized rendition. That cache is one per browser, not one per identity, and WebKit ignores Clear-Site-Data, so
    // a fresh copy would outlive a sign-out and be handed to the next identity without a request. With no-cache every
    // reuse revalidates, and the 304 comes only after the route's access gate (fix/asset-http-cache-isolation).

    /// <summary>
    /// A strong ETag over the identifying metadata. The parts are hashed rather than concatenated
    /// so the value stays opaque and fixed-size whatever they contain.
    /// </summary>
    public static string Etag(params object?[] parts) => $"\"{Key(parts)}\"";

    /// <summary>
    /// The same hash, unquoted, for use as a cache file name. Variant caching keys off this so a
    /// cached rendition is addressed by exactly the parts that decide the ETag — a re-uploaded or
    /// re-staged photo moves both together and can never serve a stale variant behind a fresh tag.
    /// </summary>
    public static string Key(params object?[] parts) => AssetVersion.Of(parts);

    /// <summary>
    /// Emits the validator and cache policy, then either short-circuits (304 on a match, a bodyless
    /// 200 for HEAD) or calls <paramref name="load"/> — which is the only thing that reads the blob.
    /// </summary>
    public static async Task<IResult> ConditionalAsync(
        HttpContext http,
        string etag,
        string? contentType,
        long length,
        Func<Task<byte[]?>> load)
    {
        var type = string.IsNullOrEmpty(contentType) ? "image/jpeg" : contentType;

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = MutableCacheControl;

        if (Matches(http.Request, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // HEAD is answered from the metadata too: the length is already known, so the blob stays
        // where it is. Kestrel would suppress the body anyway, but not the read behind it.
        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentType = type;
            http.Response.ContentLength = length;
            return Results.Empty;
        }

        var bytes = await load();
        return bytes is null ? NotFound(http) : Results.File(bytes, type);
    }

    /// <summary>
    /// The same conditional handling for a downscaled rendition. The variant's own length is not
    /// known from the image's metadata, so — unlike <see cref="ConditionalAsync"/> — a HEAD has to
    /// resolve it; that still costs at most one cached file read, never a re-render. A 304 is
    /// answered before <paramref name="load"/> is called at all, so a browser that already holds
    /// the rendition touches neither the cache nor the database.
    /// </summary>
    public static async Task<IResult> VariantAsync(
        HttpContext http,
        string etag,
        string? originalContentType,
        Func<Task<ImageVariant?>> load)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = MutableCacheControl;

        if (Matches(http.Request, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var variant = await load();
        if (variant is null)
        {
            return NotFound(http);
        }

        // A source already at or below the requested width comes back untouched, still under its own
        // stored content type — serving the original is the correct answer to "no smaller than this".
        var type = variant.IsOriginal
            ? (string.IsNullOrEmpty(originalContentType) ? "image/jpeg" : originalContentType)
            : variant.ContentType;

        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentType = type;
            http.Response.ContentLength = variant.Bytes.LongLength;
            return Results.Empty;
        }

        return Results.File(variant.Bytes, type);
    }

    /// <summary>
    /// The one entry point the byte routes use: serves the stored original when
    /// <paramref name="width"/> is null, and a cached rendition at that width otherwise. Every route
    /// resolves its <paramref name="tag"/> under its own access gate first, so the variant sits
    /// behind exactly the check the original does.
    /// </summary>
    /// <remarks>
    /// With no width the ETag is byte-identical to the one this route emitted before variants
    /// existed — <paramref name="identity"/> followed by the tag's version parts — so an existing
    /// browser cache is not invalidated. With a width the tag is a hash of the same parts PLUS the
    /// width, so the two resolutions can never be confused for one another.
    /// </remarks>
    public static Task<IResult> ServeAsync(
        HttpContext http,
        IImageVariantCache variants,
        int? width,
        WallPhotoTag tag,
        Func<Task<byte[]?>> load,
        params object?[] identity)
    {
        if (width is not { } w)
        {
            return ConditionalAsync(
                http, Etag([.. identity, .. VersionParts(tag)]), tag.ContentType, tag.Length, load);
        }

        var key = VariantKey(tag, identity);

        return VariantAsync(
            http,
            Etag(key.Identity, key.Version, w),
            tag.ContentType,
            () => variants.GetOrCreateAsync(key, w, load, http.RequestAborted));
    }

    /// <summary>
    /// The cache key a variant request for this image would resolve to. The one derivation both the
    /// byte routes and the cache warmer use — a warmed entry addressed by anything else would never
    /// be found by a real request.
    /// </summary>
    /// <remarks>
    /// A tag that carries its content hash keys the rendition by that hash (shared with the hashed route and every
    /// other identity serving the same bytes); the ETag-parts key is kept beside it so the cache can move a rendition
    /// cached before the hash existed (dual-read).
    /// </remarks>
    public static ImageVariantKey VariantKey(WallPhotoTag tag, params object?[] identity) =>
        new(Key(identity), Key(VersionParts(tag)), tag.Sha);

    /// <summary>
    /// The cache key for an uploaded gallery image, whose bytes live in the file store rather than
    /// in a blob column and so has no <see cref="WallPhotoTag"/>. Written once and never rewritten,
    /// so its size and capture time identify it for good.
    /// </summary>
    public static ImageVariantKey UploadedGalleryKey(
        Guid imageId, long sizeBytes, DateTimeOffset capturedAt, string? contentType, byte[]? sha) =>
        new(
            Key(ImageIdentity.UploadedGalleryImage(imageId)),
            Key(sizeBytes, capturedAt.UtcTicks, contentType),
            ContentHash.ToHex(sha));

    /// <summary>
    /// The parts of <paramref name="tag"/> that say which bytes are stored. Shared by the plain
    /// ETag and the variant key so the two can never drift apart.
    /// </summary>
    private static object?[] VersionParts(WallPhotoTag tag) => [tag.Version, tag.Length, tag.ContentType];

    /// <summary>
    /// Rejects a width that is not on <see cref="ImageVariants.Widths"/>. An open width parameter is
    /// a free CPU-and-disk amplifier: one request per integer would render and cache a full-size
    /// wall photo each time.
    /// </summary>
    public static bool IsRenderableWidth(int? width) => width is null || ImageVariants.IsAllowed(width.Value);

    /// <summary>
    /// Whether the caller already holds these bytes. Parsed by hand rather than through
    /// <c>EntityTagHeaderValue</c> so a malformed header degrades into a cache miss instead of an
    /// exception; the weak prefix is stripped because our own tags are always strong.
    /// </summary>
    internal static bool Matches(HttpRequest request, string etag)
    {
        foreach (var header in request.Headers.IfNoneMatch)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            foreach (var part in header.Split(','))
            {
                var candidate = part.Trim();
                if (candidate.StartsWith("W/", StringComparison.Ordinal))
                {
                    candidate = candidate[2..];
                }

                if (candidate == "*" || string.Equals(candidate, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A 404 with the validator and cache policy taken back off. Both are written up front, before
    /// the bytes are known to exist, so an archived image whose blob has gone missing would
    /// otherwise be answered 404 under the route's cache policy and validator, which a browser could
    /// then revalidate into a stored failure.
    /// </summary>
    internal static IResult NotFound(HttpContext http)
    {
        http.Response.Headers.Remove(HeaderNames.ETag);
        http.Response.Headers.Remove(HeaderNames.CacheControl);
        return Results.NotFound();
    }
}
