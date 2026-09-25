// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Security.Cryptography;
using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The training bundle a runner downloads: the splat worker's <c>bundle.zip</c> from its
/// <c>splat-prepare</c> job, REBUILT here entry by entry so that nothing but the expected layout
/// reaches a machine outside the server, and every image is metadata-stripped once more (no GPS,
/// no camera serial survives even if the worker let one through).
/// </summary>
/// <remarks>
/// Allowed: <c>train.json</c>, <c>dataset/images/…/*.jpg|jpeg|png</c>, <c>dataset/sparse/…/*.bin|txt</c>.
/// Anything else, a path that escapes (<c>..</c>, rooted, backslashes), too many entries or too many
/// bytes refuses the bundle. Works stream to stream (files on disk): only one entry is ever in memory.
/// </remarks>
public static class RunnerBundle
{
    public const int MaxEntries = 20000;
    public const long MaxUncompressedBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxEntryBytes = 256L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];
    private static readonly string[] SparseExtensions = [".bin", ".txt"];

    private enum EntryKind
    {
        Options,
        Image,
        Sparse,
    }

    /// <summary>Rebuilds the (seekable) zip <paramref name="zip"/> into <paramref name="output"/>; throws <see cref="InvalidDataException"/>.</summary>
    public static void Sanitize(Stream zip, Stream output)
    {
        using var input = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        if (input.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"The training bundle has more than {MaxEntries} entries.");
        }

        if (input.GetEntry("train.json") is null)
        {
            throw new InvalidDataException("The training bundle has no train.json.");
        }

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        long total = 0;
        var images = 0;
        foreach (var entry in input.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            var kind = Classify(entry.FullName);
            var bytes = ReadEntry(entry);
            total += bytes.LongLength;
            if (total > MaxUncompressedBytes)
            {
                throw new InvalidDataException("The training bundle is too large.");
            }

            if (kind == EntryKind.Image)
            {
                bytes = ImageMetadataStripper.Strip(bytes);
                images++;
            }

            var copy = archive.CreateEntry(entry.FullName, kind == EntryKind.Image ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            copy.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = copy.Open();
            stream.Write(bytes);
        }

        if (images == 0)
        {
            throw new InvalidDataException("The training bundle has no images.");
        }
    }

    /// <summary>The sanitized bundle and its SHA-256 (hex), in memory (small bundles, tests).</summary>
    public static (byte[] Bytes, string Sha256) Sanitize(byte[] zip)
    {
        using var input = new MemoryStream(zip, writable: false);
        using var output = new MemoryStream();
        Sanitize(input, output);
        var result = output.ToArray();
        return (result, Convert.ToHexStringLower(SHA256.HashData(result)));
    }

    /// <summary>Hex SHA-256 of a stored file (what the runner checks after its download).</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    private static EntryKind Classify(string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/') || name.Contains('\\')
            || name.Contains(':') || name.Length > 300)
        {
            throw new InvalidDataException($"The training bundle has an unsafe path: {name}");
        }

        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (name == "train.json")
        {
            return EntryKind.Options;
        }

        if (name.StartsWith("dataset/images/", StringComparison.Ordinal) && ImageExtensions.Contains(ext))
        {
            return EntryKind.Image;
        }

        if (name.StartsWith("dataset/sparse/", StringComparison.Ordinal) && SparseExtensions.Contains(ext))
        {
            return EntryKind.Sparse;
        }

        throw new InvalidDataException($"The training bundle has an unexpected file: {name}");
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException($"The training bundle entry {entry.FullName} is too large.");
        }

        // The header's length may lie (a zip bomb): read at most one byte past the entry limit.
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(entry.Length, MaxEntryBytes));
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxEntryBytes)
            {
                throw new InvalidDataException($"The training bundle entry {entry.FullName} is too large.");
            }
        }

        return buffer.ToArray();
    }
}
