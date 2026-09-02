namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Downscaled renditions of a stored image, derived on demand and cached so the browser is sent
/// only the resolution its viewport actually needs.
/// </summary>
/// <remarks>
/// The stored image is always the camera original — hold detection, alignment and the big-wall
/// panel matcher all read it, and their accuracy depends on that resolution. Variants are pure
/// derived data: they carry no database row, are keyed by the same version token the image's ETag
/// is built from, and can be deleted at any time without losing anything.
/// </remarks>
public interface IImageVariantCache
{
    /// <summary>
    /// The rendition of <paramref name="key"/> at <paramref name="width"/> pixels, generating and
    /// caching it on the first request. Returns the original bytes untouched when the source is
    /// already at or below the requested width — a variant is never an upscale — and null when the
    /// original cannot be loaded.
    /// </summary>
    /// <param name="key">Identity and version of the source image.</param>
    /// <param name="width">Target width in pixels. Callers must have validated it against
    /// <c>ImageVariants.Widths</c>; anything else is a request for an unbounded render.</param>
    /// <param name="loadOriginal">Reads the stored original. Called only on a cache miss.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ImageVariant?> GetOrCreateAsync(
        ImageVariantKey key,
        int width,
        Func<Task<byte[]?>> loadOriginal,
        CancellationToken ct);
}

/// <summary>
/// What a cached variant is addressed by.
/// </summary>
/// <param name="Identity">
/// Which image this is — the wall and slot, or the panel and slot. Stable across re-uploads, so
/// every rendition of one image shares a directory and a stale generation can be swept.
/// </param>
/// <param name="Version">
/// A token over the exact bytes currently stored, built from the same parts as the image's ETag
/// (generation or staging timestamp, length, content type). A re-uploaded or re-staged photo
/// therefore lands on a different cache file and can never serve a stale variant.
/// </param>
public sealed record ImageVariantKey(string Identity, string Version);

/// <summary>A rendition's bytes and the type they are encoded in.</summary>
/// <param name="Bytes">The encoded image.</param>
/// <param name="ContentType">Content type of <paramref name="Bytes"/>.</param>
/// <param name="IsOriginal">
/// True when the source was already at or below the requested width and is being served as-is.
/// </param>
public sealed record ImageVariant(byte[] Bytes, string ContentType, bool IsOriginal);
