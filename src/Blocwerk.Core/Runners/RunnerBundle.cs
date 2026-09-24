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
/// bytes refuses the bundle.
/// </remarks>
public static class RunnerBundle
{
    public const int MaxEntries = 20000;
    public const long MaxUncompressedBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxEntryBytes = 256L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];
    private static readonly string[] SparseExtensions = [".bin", ".txt"];

    /// <summary>The sanitized bundle and its SHA-256 (hex).</summary>
    public static (byte[] Bytes, string Sha256) Sanitize(byte[] zip)
    {
        using var input = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (input.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"The training bundle has more than {MaxEntries} entries.");
        }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
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

            if (images == 0 || input.GetEntry("train.json") is null)
            {
                throw new InvalidDataException("The training bundle has no images or no train.json.");
            }
        }

        var result = output.ToArray();
        return (result, Convert.ToHexStringLower(SHA256.HashData(result)));
    }

    private enum EntryKind
    {
        Options,
        Image,
        Sparse,
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

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
