// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// Remembers the content hash of files already hashed, keyed by path, length and last write time, so a streamed asset
/// (a splat of tens of megabytes, fetched again for every Range request) is hashed once rather than per request. A
/// rewrite moves the write time and so misses the memo. Bounded: it starts over past <see cref="Capacity"/> entries.
/// </summary>
public sealed class FileHashMemo
{
    /// <summary>Entries kept before the memo is cleared.</summary>
    public const int Capacity = 4096;

    private readonly ConcurrentDictionary<(string Path, long Length, long WriteTicks), byte[]> hashes = new();

    /// <summary>Whether the file at <paramref name="path"/> hashes to <paramref name="sha"/>; false when it is gone.</summary>
    public async Task<bool> MatchesAsync(string path, byte[] sha, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return false;
        }

        var key = (path, info.Length, info.LastWriteTimeUtc.Ticks);
        if (!hashes.TryGetValue(key, out var hash))
        {
            hash = await ContentHash.OfFileAsync(path, ct);
            if (hash is null)
            {
                return false;
            }

            if (hashes.Count >= Capacity)
            {
                hashes.Clear();
            }

            hashes[key] = hash;
        }

        return ContentHash.Same(hash, sha);
    }
}
