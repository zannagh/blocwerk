using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Disk-backed <see cref="IImageVariantCache"/> under <c>WallImageSettings.StoragePath</c>, in a
/// <c>variants</c> subfolder next to the uploaded gallery images.
/// </summary>
/// <remarks>
/// Nothing here is authoritative. The cache holds only renditions that can be rebuilt from the
/// stored original, so it needs no database row, no migration and no backup; deleting the whole
/// folder costs one re-render per image. Files are laid out as
/// <c>variants/{identity}/{version}-{width}.{jpg|webp}</c>: one directory per image, so a new
/// version's renditions can sweep the previous version's in the same pass that writes them instead
/// of accumulating forever.
/// <para>
/// The version half of the key is built from exactly the parts the image's ETag is built from, so a
/// cached rendition is precisely as fresh as the 304 the same request would have received. It can
/// never be MORE stale than the existing conditional path — where that path would wrongly answer
/// 304, this one would wrongly answer with a matching rendition, and where it revalidates
/// correctly, this one re-renders. See <see cref="WallPhotoTag"/> for what those parts are and the
/// invariants they rest on.
/// </para>
/// <para>
/// A source whose row has a content hash (<see cref="ImageVariantKey.Sha"/>) is keyed by the hash instead:
/// <c>variants/sha/{sha[..2]}/{sha}-{width}.{jpg|webp}</c>. One rendition then serves every route that serves those
/// bytes, and nothing needs sweeping, since a new photo is a new hash. Renditions of hashes no row holds any more are
/// not collected yet; they are small, and deleting the folder is always safe.
/// </para>
/// </remarks>
public sealed class FileSystemImageVariantCache : IImageVariantCache
{
    /// <summary>The folder of the hashed renditions, next to the per-identity folders of the legacy ones.</summary>
    private const string ShaFolder = "sha";

    private readonly string root;
    private readonly string tempRoot;
    private readonly ILogger<FileSystemImageVariantCache> logger;

    public FileSystemImageVariantCache(BlocwerkSettings settings, ILogger<FileSystemImageVariantCache> logger)
    {
        this.logger = logger;
        root = Path.Combine(Path.GetFullPath(settings.WallImage.StoragePath), "variants");
        tempRoot = Path.Combine(root, "tmp");
        Directory.CreateDirectory(tempRoot);
    }

    /// <inheritdoc/>
    public Task<ImageVariant?> GetOrCreateAsync(
        ImageVariantKey key,
        int width,
        Func<Task<byte[]?>> loadOriginal,
        CancellationToken ct)
    {
        // Belt and braces over the endpoint's own check: an unvalidated width reaching here would
        // be an unbounded render.
        if (!ImageVariants.IsAllowed(width))
        {
            return Task.FromResult<ImageVariant?>(null);
        }

        return key.Sha is null
            ? GetOrCreateLegacyAsync(key, width, loadOriginal, ct)
            : GetOrCreateHashedAsync(key, width, loadOriginal, ct);
    }

    /// <inheritdoc/>
    public (long Length, string ContentType)? PeekHashed(ImageVariantKey key, int width)
    {
        if (key.Sha is not { } sha || !ImageVariantNames.IsSha(sha) || !ImageVariants.IsAllowed(width))
        {
            return null;
        }

        var stem = Path.Combine(root, ShaFolder, sha[..2], $"{sha}-{width}");
        foreach (var extension in ImageRendition.Extensions)
        {
            var file = new FileInfo(stem + extension);
            if (file.Exists)
            {
                return (file.Length, ImageVariantNames.TypeOf(extension));
            }
        }

        return null;
    }

    /// <summary>A source with no content hash yet: keyed by its ETag parts, one directory per image.</summary>
    private async Task<ImageVariant?> GetOrCreateLegacyAsync(
        ImageVariantKey key, int width, Func<Task<byte[]?>> loadOriginal, CancellationToken ct)
    {
        // An identity that is not a plain hash would be a path.
        if (!ImageVariantNames.IsHash(key.Identity) || !ImageVariantNames.IsHash(key.Version))
        {
            return null;
        }

        var directory = Path.Combine(root, key.Identity);
        var stem = Path.Combine(directory, $"{key.Version}-{width}");

        var cached = await TryReadAsync(stem, ct);
        if (cached is not null)
        {
            return cached;
        }

        var original = await loadOriginal();
        if (original is not { Length: > 0 })
        {
            return null;
        }

        var rendered = ImageRendition.Render(original, width);
        if (rendered is null)
        {
            // Already narrow enough, or not a decodable image. Either way the original IS the best
            // answer, and caching a copy of it under a variant name would only waste the disk.
            return new ImageVariant(original, string.Empty, IsOriginal: true);
        }

        await WriteAsync(directory, stem + rendered.Extension, key.Version, rendered.Bytes, ct);
        return new ImageVariant(rendered.Bytes, rendered.ContentType, IsOriginal: false);
    }

    /// <summary>
    /// A hashed source, keyed by <c>(sha, width)</c> under <c>variants/sha/{sha[..2]}/</c>. On a miss the rendition
    /// the legacy key names is moved over (dual-read), so a deploy does not re-render every cached variant. A fresh
    /// rendition is cached only when the original really hashes to the key's sha: a photo rewritten between the
    /// caller's metadata read and its byte read must never land under the old hash.
    /// </summary>
    private async Task<ImageVariant?> GetOrCreateHashedAsync(
        ImageVariantKey key, int width, Func<Task<byte[]?>> loadOriginal, CancellationToken ct)
    {
        var sha = key.Sha!;
        if (!ImageVariantNames.IsSha(sha))
        {
            return null;
        }

        var directory = Path.Combine(root, ShaFolder, sha[..2]);
        var stem = Path.Combine(directory, $"{sha}-{width}");

        var cached = await TryReadAsync(stem, ct) ?? await TryAdoptLegacyAsync(key, width, directory, stem, ct);
        if (cached is not null)
        {
            return cached;
        }

        var original = await loadOriginal();
        if (original is not { Length: > 0 })
        {
            return null;
        }

        var rendered = ImageRendition.Render(original, width);
        if (rendered is null)
        {
            return new ImageVariant(original, string.Empty, IsOriginal: true);
        }

        if (ContentHash.ToHex(ContentHash.Of(original)) == sha)
        {
            await WriteAsync(directory, stem + rendered.Extension, version: null, rendered.Bytes, ct);
        }
        else
        {
            logger.LogWarning("Image bytes no longer hash to {Sha}; the rendition is served but not cached", sha);
        }

        return new ImageVariant(rendered.Bytes, rendered.ContentType, IsOriginal: false);
    }

    /// <summary>
    /// Moves the rendition stored under the legacy ETag-parts key into the hashed slot and reads it from there. A
    /// rename, not a copy; a lost race with another request doing the same just means the hashed slot is now full.
    /// </summary>
    private async Task<ImageVariant?> TryAdoptLegacyAsync(
        ImageVariantKey key, int width, string directory, string stem, CancellationToken ct)
    {
        if (!ImageVariantNames.IsHash(key.Identity) || !ImageVariantNames.IsHash(key.Version))
        {
            return null;
        }

        var legacyStem = Path.Combine(root, key.Identity, $"{key.Version}-{width}");
        foreach (var extension in ImageRendition.Extensions)
        {
            var legacy = legacyStem + extension;
            if (!File.Exists(legacy))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(directory);
                File.Move(legacy, stem + extension, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not move legacy image variant {Path}", legacy);
            }

            return await TryReadAsync(stem, ct);
        }

        return null;
    }

    /// <summary>
    /// Reads whichever encoding this rendition was written under. Two probes rather than one
    /// because the format follows the SOURCE — a transparent original is kept as WebP, everything
    /// else is JPEG — and which it was is not knowable without loading the original, which is the
    /// very thing a cache hit exists to avoid.
    /// </summary>
    private static async Task<ImageVariant?> TryReadAsync(string stem, CancellationToken ct)
    {
        foreach (var extension in ImageRendition.Extensions)
        {
            var path = stem + extension;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path, ct);
                return new ImageVariant(bytes, ImageVariantNames.TypeOf(extension), IsOriginal: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Treated as a miss: the rendition is regenerated rather than failing the request.
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the rendition, then sweeps the renditions of superseded versions of the same image.
    /// The write goes to a uniquely named temp file and is committed by a rename, so two requests
    /// racing on the same uncached variant either both publish identical bytes or one overwrites
    /// the other — a reader can never observe a half-written file.
    /// </summary>
    private async Task WriteAsync(string directory, string path, string? version, byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
            if (version is not null)
            {
                Sweep(directory, version);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is a performance problem, not a correctness one: the
            // rendition was already produced and is on its way to the caller.
            logger.LogWarning(ex, "Could not cache image variant at {Path}", path);
            TryDelete(temp);
        }
    }

    /// <summary>Drops renditions of every version of this image except the current one.</summary>
    private void Sweep(string directory, string version)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory))
            {
                if (!Path.GetFileName(stale).StartsWith(version, StringComparison.Ordinal))
                {
                    TryDelete(stale);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not sweep stale image variants in {Directory}", directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do; a leftover file is swept on the next write.
        }
    }
}
