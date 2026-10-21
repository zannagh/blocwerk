// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// Remembers which database blobs have already been hashed in full and found to match their sha, so a 7 MB panel
/// photo behind a public share link costs one SHA-256 per process instead of one per 200.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guarantee</b> is that of the query, not of this memo: a blob is always read with
/// <c>WHERE Id = @id AND hash = @sha</c>, so the bytes served come from a row version whose hash column held the sha.
/// Correct writers move bytes and hash together (the content-hash stamp on every save, the bulk writers in the same
/// <c>SetProperty</c>, the backfill only where the hash is null), so that match is the real proof.
/// </para>
/// <para>
/// The full rehash on top of it is a backstop against a buggy writer that changed the bytes and left the hash. The memo
/// keeps a heuristic version of that backstop instead of repeating it: an entry is keyed by
/// <c>(kind, row, sha, length)</c> and holds a fingerprint (SHA-256 over the first and last <see cref="EdgeBytes"/>).
/// A later read is trusted only when its length and fingerprint both match, and is hashed in full otherwise. A buggy
/// rewrite is therefore caught unless it kept the length and both edges, which an image re-encode or a metadata strip
/// (the header) does not. The memo holds no bytes and is bounded by <see cref="Capacity"/>.
/// </para>
/// </remarks>
public sealed class VerifiedBlobMemo
{
    /// <summary>Entries kept before the memo is cleared.</summary>
    public const int Capacity = 4096;

    /// <summary>How many bytes from each end of a blob the fingerprint covers.</summary>
    public const int EdgeBytes = 64 * 1024;

    private readonly ConcurrentDictionary<(AssetKind Kind, Guid RowId, string Sha, long Length), byte[]> verified = new();

    private long fullHashes;

    /// <summary>How many blobs this memo has hashed in full (a diagnostic, and what the tests count).</summary>
    public long FullHashes => Interlocked.Read(ref fullHashes);

    /// <summary>
    /// Whether <paramref name="bytes"/>, read for <paramref name="asset"/>, hash to its sha: from the memo when the
    /// same row, sha, length and edges were verified before, otherwise by hashing them in full.
    /// </summary>
    public bool Verify(ResolvedAsset asset, byte[] bytes)
    {
        var key = (asset.Kind, asset.RowId, asset.Hex, bytes.LongLength);
        var fingerprint = Fingerprint(bytes);
        if (verified.TryGetValue(key, out var known) && known.AsSpan().SequenceEqual(fingerprint))
        {
            return true;
        }

        Interlocked.Increment(ref fullHashes);
        if (!ContentHash.Same(ContentHash.Of(bytes), asset.Sha256))
        {
            verified.TryRemove(key, out _);
            return false;
        }

        if (verified.Count >= Capacity)
        {
            verified.Clear();
        }

        verified[key] = fingerprint;
        return true;
    }

    private static byte[] Fingerprint(byte[] bytes)
    {
        if (bytes.Length <= 2 * EdgeBytes)
        {
            return SHA256.HashData(bytes);
        }

        using var edges = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        edges.AppendData(bytes, 0, EdgeBytes);
        edges.AppendData(bytes, bytes.Length - EdgeBytes, EdgeBytes);
        return edges.GetHashAndReset();
    }
}
