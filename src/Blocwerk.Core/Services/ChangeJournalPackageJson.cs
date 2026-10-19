// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;

namespace Blocwerk.Core.Services;

/// <summary>
/// The wire format of a <see cref="ChangeJournalPackage"/>: JSON with the web defaults (the same shape the Development
/// <c>/dev/journal/export</c> endpoint writes, so a package from either side replays on the other), gzip-compressed for
/// the admin API. Reading accepts either, told apart by the gzip magic bytes rather than a header.
/// </summary>
public static class ChangeJournalPackageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Writes <paramref name="package"/> as gzip-compressed JSON to <paramref name="destination"/>.</summary>
    public static async Task WriteGzipAsync(ChangeJournalPackage package, Stream destination, CancellationToken ct)
    {
        await using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        await JsonSerializer.SerializeAsync(gzip, package, Options, ct);
    }

    /// <summary>
    /// Reads a package from a SEEKABLE stream holding gzip-compressed or plain JSON. Throws <see cref="JsonException"/>
    /// when the body is empty or not a package, and <see cref="InvalidDataException"/> for a broken gzip stream.
    /// </summary>
    public static async Task<ChangeJournalPackage> ReadAsync(Stream source, CancellationToken ct)
    {
        if (!source.CanSeek)
        {
            throw new ArgumentException("The package stream must be seekable (buffer the request body first).", nameof(source));
        }

        var start = source.Position;
        var magic = new byte[2];
        var read = await source.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, ct);
        source.Position = start;

        var isGzip = read == 2 && magic[0] == 0x1f && magic[1] == 0x8b;
        await using var body = isGzip ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : null;
        return await JsonSerializer.DeserializeAsync<ChangeJournalPackage>(body ?? source, Options, ct)
               ?? throw new JsonException("The body is empty.");
    }
}
