using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Abstractions;
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

    /// <summary>
    /// An image that can never change again — a retired generation's archived photo, addressed by
    /// its generation number. Still <c>private</c>, but the browser may serve it for a year without
    /// even revalidating.
    /// </summary>
    public const string ImmutableCacheControl = "private, max-age=31536000, immutable";

    /// <summary>The unit separator, used only to keep the hashed parts from running together.</summary>
    private const char PartSeparator = '\u001f';

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
    public static string Key(params object?[] parts)
    {
        // Formatted invariantly, never under the ambient culture. Today's parts are GUIDs, ints and
        // ASCII content types that format identically everywhere, but a part whose ToString() IS
        // culture-sensitive (a date, a decimal) would otherwise make the tag depend on the server's
        // locale — silently invalidating every browser and disk cache the moment that changed.
        var raw = string.Join(
            PartSeparator,
            parts.Select(p => p is IFormattable f
                ? f.ToString(null, CultureInfo.InvariantCulture)
                : p?.ToString() ?? string.Empty));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Emits the validator and cache policy, then either short-circuits (304 on a match, a bodyless
    /// 200 for HEAD) or calls <paramref name="load"/> — which is the only thing that reads the blob.
    /// </summary>
    public static async Task<IResult> ConditionalAsync(
        HttpContext http,
        string etag,
        string? contentType,
        long length,
        bool immutable,
        Func<Task<byte[]?>> load)
    {
        var type = string.IsNullOrEmpty(contentType) ? "image/jpeg" : contentType;

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = immutable ? ImmutableCacheControl : MutableCacheControl;

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
        bool immutable,
        Func<Task<ImageVariant?>> load)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = immutable ? ImmutableCacheControl : MutableCacheControl;

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
        bool immutable,
        Func<Task<byte[]?>> load,
        params object?[] identity)
    {
        object?[] versionParts = [tag.Version, tag.Length, tag.ContentType];

        if (width is not { } w)
        {
            return ConditionalAsync(
                http, Etag([.. identity, .. versionParts]), tag.ContentType, tag.Length, immutable, load);
        }

        var key = new ImageVariantKey(Key(identity), Key(versionParts));

        return VariantAsync(
            http,
            Etag(key.Identity, key.Version, w),
            tag.ContentType,
            immutable,
            () => variants.GetOrCreateAsync(key, w, load, http.RequestAborted));
    }

    /// <summary>
    /// Rejects a width that is not on <see cref="ImageVariants.Widths"/>. An open width parameter is
    /// a free CPU-and-disk amplifier: one request per integer would render and cache a full-size
    /// wall photo each time.
    /// </summary>
    public static bool IsRenderableWidth(int? width) => width is null || ImageVariants.IsAllowed(width.Value);

    /// <summary>
    /// A 404 with the validator and cache policy taken back off. Both are written up front, before
    /// the bytes are known to exist, so an archived image whose blob has gone missing would
    /// otherwise be answered 404 under <c>max-age=31536000, immutable</c> — telling the browser to
    /// hold that failure for a year and never ask again, even once the image is restored.
    /// </summary>
    private static IResult NotFound(HttpContext http)
    {
        http.Response.Headers.Remove(HeaderNames.ETag);
        http.Response.Headers.Remove(HeaderNames.CacheControl);
        return Results.NotFound();
    }

    /// <summary>
    /// Whether the caller already holds these bytes. Parsed by hand rather than through
    /// <c>EntityTagHeaderValue</c> so a malformed header degrades into a cache miss instead of an
    /// exception; the weak prefix is stripped because our own tags are always strong.
    /// </summary>
    private static bool Matches(HttpRequest request, string etag)
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
}
