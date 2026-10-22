// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;

namespace Blocwerk.Core.Services;

/// <summary>The files of <see cref="FileSystemImageVariantCache"/>: which key parts are safe in a path, and reading and deleting one.</summary>
internal static class ImageVariantFiles
{
    /// <summary>
    /// Lowercase hex only. Both key parts are hashes produced by the byte routes, so anything else
    /// is either a bug or an attempt to escape the cache root.
    /// </summary>
    public static bool IsHash(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A content hash: exactly 64 lowercase hex characters.</summary>
    public static bool IsSha(string value) => value.Length == ContentHash.Length * 2 && IsHash(value);

    /// <summary>The content type a rendition stored under <paramref name="extension"/> is served as.</summary>
    public static string TypeOf(string extension) => extension == ".webp" ? "image/webp" : "image/jpeg";

    /// <summary>
    /// Reads whichever encoding this rendition was written under. Two probes rather than one
    /// because the format follows the SOURCE — a transparent original is kept as WebP, everything
    /// else is JPEG — and which it was is not knowable without loading the original, which is the
    /// very thing a cache hit exists to avoid.
    /// </summary>
    public static async Task<ImageVariant?> TryReadAsync(string stem, CancellationToken ct)
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
                return new ImageVariant(bytes, TypeOf(extension), IsOriginal: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Treated as a miss: the rendition is regenerated rather than failing the request.
                return null;
            }
        }

        return null;
    }

    public static void TryDelete(string path)
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
