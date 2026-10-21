// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// Remembers the content hash of files already hashed, keyed by path, length and last write time, so a streamed asset
/// (a splat of tens of megabytes, fetched again for every Range request) or a buffered one (a gallery photo, a
/// texture) is hashed once rather than per request. A rewrite moves the write time and so misses the memo. Bounded: it
/// starts over past <see cref="Capacity"/> entries.
/// </summary>
/// <remarks>
/// Single-flight: concurrent requests for a file not yet hashed share one hash computation (one task per key) instead
/// of each reading the whole file. The shared computation is not cancelled by any one caller, who only stops waiting
/// for it; it is bounded by the file's size and its result serves every later request. A failed or empty computation
/// is dropped, so the next request tries again.
/// </remarks>
public sealed class FileHashMemo(Func<string, CancellationToken, Task<byte[]?>>? hashFile = null)
{
    /// <summary>Entries kept before the memo is cleared.</summary>
    public const int Capacity = 4096;

    private readonly Func<string, CancellationToken, Task<byte[]?>> hash = hashFile ?? ContentHash.OfFileAsync;

    private readonly ConcurrentDictionary<FileStamp, Lazy<Task<byte[]?>>> hashes = new();

    private long fullHashes;

    /// <summary>How many files (streamed or buffered) this memo has hashed in full: a diagnostic, and what tests count.</summary>
    public long FullHashes => Interlocked.Read(ref fullHashes);

    /// <summary>Whether the file at <paramref name="path"/> hashes to <paramref name="sha"/>; false when it is gone.</summary>
    public async Task<bool> MatchesAsync(string path, byte[] sha, CancellationToken ct)
    {
        if (FileStamp.Of(path) is not { } key)
        {
            return false;
        }

        var entry = hashes.GetOrAdd(key, k => new Lazy<Task<byte[]?>>(() => HashAsync(k)));
        if (hashes.Count > Capacity)
        {
            hashes.Clear();
        }

        var computed = await entry.Value.WaitAsync(ct);
        return ContentHash.Same(computed, sha);
    }

    /// <summary>
    /// The bytes of the file at <paramref name="path"/> if they hash to <paramref name="sha"/>; otherwise null. The
    /// bytes are only trusted from the memo when the file's stamp is the same before and after the read and the read
    /// returned the stamped length; anything else (a rewrite during the read) hashes the bytes that were read.
    /// </summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    public async Task<byte[]?> ReadVerifiedAsync(string path, byte[] sha, CancellationToken ct)
    {
        if (FileStamp.Of(path) is not { } before)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var stable = FileStamp.Of(path) == before && bytes.LongLength == before.Length;
        if (stable && Known(before) is { } known)
        {
            return ContentHash.Same(known, sha) ? bytes : null;
        }

        Interlocked.Increment(ref fullHashes);
        var computed = ContentHash.Of(bytes)!;
        if (stable)
        {
            hashes[before] = new Lazy<Task<byte[]?>>(Task.FromResult<byte[]?>(computed));
        }

        return ContentHash.Same(computed, sha) ? bytes : null;
    }

    /// <summary>A hash already computed for <paramref name="key"/>, without waiting on one still in flight.</summary>
    private byte[]? Known(FileStamp key) =>
        hashes.TryGetValue(key, out var entry) && entry.IsValueCreated && entry.Value.IsCompletedSuccessfully
            ? entry.Value.Result
            : null;

    private async Task<byte[]?> HashAsync(FileStamp key)
    {
        Interlocked.Increment(ref fullHashes);
        try
        {
            var computed = await hash(key.Path, CancellationToken.None);
            if (computed is null)
            {
                Forget(key);
            }

            return computed;
        }
        catch
        {
            Forget(key);
            throw;
        }
    }

    private void Forget(FileStamp key) => hashes.TryRemove(key, out _);
}
