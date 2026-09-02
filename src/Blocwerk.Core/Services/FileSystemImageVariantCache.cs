using Blocwerk.Core.Abstractions;
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
/// </remarks>
public sealed class FileSystemImageVariantCache : IImageVariantCache
{
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
    public async Task<ImageVariant?> GetOrCreateAsync(
        ImageVariantKey key,
        int width,
        Func<Task<byte[]?>> loadOriginal,
        CancellationToken ct)
    {
        // Belt and braces over the endpoint's own check: an unvalidated width reaching here would
        // be an unbounded render, and an identity that is not a plain hash would be a path.
        if (!ImageVariants.IsAllowed(width) || !IsHash(key.Identity) || !IsHash(key.Version))
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
                var type = extension == ".webp" ? "image/webp" : "image/jpeg";
                return new ImageVariant(bytes, type, IsOriginal: false);
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
    private async Task WriteAsync(string directory, string path, string version, byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
            Sweep(directory, version);
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

    /// <summary>
    /// Lowercase hex only. Both key parts are hashes produced by the byte routes, so anything else
    /// is either a bug or an attempt to escape the cache root.
    /// </summary>
    private static bool IsHash(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
