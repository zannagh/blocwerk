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
/// The guarantee of the <c>/a/</c> routes is that the bytes served hash to the sha in the URL. A blob is always read
/// with <c>WHERE Id = @id AND hash = @sha</c>, so hash and bytes come from one row version; the full rehash on top of
/// that exists only to catch a writer that changed the bytes without moving the hash (a bug, since the stamp and
/// every bulk writer set both together).
/// </para>
/// <para>
/// The memo keeps that check honest without repeating it: an entry is keyed by <c>(kind, row, sha, length)</c> and
/// holds a fingerprint of the verified bytes (SHA-256 over their first and last <see cref="EdgeBytes"/>). A later
/// read is trusted only when its length and fingerprint both match; anything else is hashed in full again. So a
/// rewrite that kept the hash is still caught unless it kept the length AND both edges too, which an image re-encode
/// or a metadata strip (the header) never does. The memo holds no bytes and is bounded by <see cref="Capacity"/>.
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
