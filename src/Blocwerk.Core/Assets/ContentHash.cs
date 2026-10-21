// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;

namespace Blocwerk.Core.Assets;

/// <summary>
/// The content hash of a served asset: SHA-256 of the exact bytes the browser receives, stored as a 32-byte column
/// next to them (database blobs) or next to the stored name (files). Postgres computes the same value with
/// <c>sha256(bytea)</c>, which is what the backfill relies on.
/// </summary>
public static class ContentHash
{
    /// <summary>Byte length of a hash.</summary>
    public const int Length = 32;

    private const int FileBufferBytes = 81920;

    /// <summary>The hash of <paramref name="bytes"/>; null for no bytes (an empty array still has a hash).</summary>
    public static byte[]? Of(byte[]? bytes) => bytes is null ? null : SHA256.HashData(bytes);

    /// <summary>
    /// The hash of a file, read as a stream (never whole in memory); null when there is no path or no file there.
    /// </summary>
    public static async Task<byte[]?> OfFileAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await SHA256.HashDataAsync(stream, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Lowercase hex, the form a hash takes in URLs and JSON; null for null.</summary>
    public static string? ToHex(byte[]? hash) => hash is null ? null : Convert.ToHexStringLower(hash);

    /// <summary>The bytes of a lowercase or uppercase hex hash; null for null or anything that is not one.</summary>
    public static byte[]? FromHex(string? hex)
    {
        if (hex is not { Length: Length * 2 })
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether two hashes hold the same bytes (both null counts as the same).</summary>
    public static bool Same(byte[]? a, byte[]? b) =>
        a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);
}
