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
    /// <param name="originalVerified">
    /// True when <paramref name="loadOriginal"/> only ever returns bytes that hash to <see cref="ImageVariantKey.Sha"/>
    /// (the hashed route's resolver, which checks them through its memo), so the cache need not hash them again.
    /// </param>
    Task<ImageVariant?> GetOrCreateAsync(
        ImageVariantKey key,
        int width,
        Func<Task<byte[]?>> loadOriginal,
        CancellationToken ct,
        bool originalVerified = false);

    /// <summary>
    /// The length and content type of the rendition of a HASHED source (<see cref="ImageVariantKey.Sha"/>) already in
    /// the cache, from the file system alone: nothing is read, loaded or rendered. Null when it is not cached yet, or
    /// for a key without a sha. What a HEAD is answered from.
    /// </summary>
    /// <param name="key">The source; only its <see cref="ImageVariantKey.Sha"/> is used.</param>
    /// <param name="width">Target width in pixels.</param>
    (long Length, string ContentType)? PeekHashed(ImageVariantKey key, int width);
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
/// <param name="Sha">
/// The content hash of the source (lowercase hex), when its row has one. A keyed rendition is stored under
/// <c>(Sha, width)</c> instead, shared by every route that serves the same bytes; <paramref name="Identity"/> and
/// <paramref name="Version"/> then only name the legacy file the cache moves over on first use (dual-read). Either
/// may be empty when the caller has no legacy key.
/// </param>
/// <param name="Alternates">
/// Further legacy <c>(Identity, Version)</c> keys the same bytes may have been cached under by other routes (a wall photo
/// is addressed three ways); a hashed miss tries each before rendering.
/// </param>
public sealed record ImageVariantKey(
    string Identity, string Version, string? Sha = null, IReadOnlyList<(string Identity, string Version)>? Alternates = null);

/// <summary>A rendition's bytes and the type they are encoded in.</summary>
/// <param name="Bytes">The encoded image.</param>
/// <param name="ContentType">Content type of <paramref name="Bytes"/>.</param>
/// <param name="IsOriginal">
/// True when the source was already at or below the requested width and is being served as-is.
/// </param>
public sealed record ImageVariant(byte[] Bytes, string ContentType, bool IsOriginal);
