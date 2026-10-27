// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The input of a <see cref="Entities.GpuJobKind.SplatPrepare"/> / <see cref="Entities.GpuJobKind.SplatFinish"/> job: the
/// multipart submission the splat worker would have received, as one zip a 3D runner downloads like any bundle. The zip holds
/// <c>parts.json</c> (<c>{"version":1,"service":"splat","kind":"splat-prepare","parts":[{"name":…,"file":…}|{"name":…,"json":…}]}</c>)
/// and one stored (uncompressed) entry per file part, named exactly like the part's file name. A JSON field travels inline
/// as <c>json</c>. The runner rebuilds the request from it and runs the same job function the HTTP worker runs; what it
/// uploads is read back by <see cref="RunnerComputeResult"/>. Written part by part from the store: no file is held in memory.
/// </summary>
public static class RunnerComputeEnvelope
{
    /// <summary>The manifest entry of the zip.</summary>
    public const string ManifestName = "parts.json";

    /// <summary>The envelope format version.</summary>
    public const int Version = 1;

    /// <summary>The compute service the envelope is for.</summary>
    public const string Service = "splat";

    private const int MaxFileNameLength = 255;

    /// <summary>The manifest document for <paramref name="parts"/> (file parts only name their entry; JSON fields are inline).</summary>
    public static string Manifest(string kind, IReadOnlyList<ComputeJobPart> parts)
    {
        var list = new JsonArray();
        foreach (var part in parts)
        {
            var node = new JsonObject { ["name"] = part.Name };
            if (IsInlineJson(part))
            {
                node["json"] = ParseJson(part);
            }
            else
            {
                node["file"] = EntryName(part);
            }

            list.Add(node);
        }

        return new JsonObject { ["version"] = Version, ["service"] = Service, ["kind"] = kind, ["parts"] = list }.ToJsonString();
    }

    /// <summary>
    /// Streams the envelope into <paramref name="output"/>: the manifest first, then the file parts in order. A file part without
    /// a plain file name, a name taken twice, or a JSON part that is not JSON throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task WriteAsync(Stream output, string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct)
    {
        var manifest = Manifest(kind, parts);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteEntryAsync(zip, ManifestName, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest)), ct);
        foreach (var part in parts.Where(p => !IsInlineJson(p)))
        {
            var name = EntryName(part);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"Two parts of the envelope are named {name}.");
            }

            await WriteEntryAsync(zip, name, part.SourcePath is { } path ? File.OpenRead(path) : new MemoryStream(part.Content, writable: false), ct);
        }
    }

    /// <summary>
    /// Writes the envelope into the store; returns its stored name, size and SHA-256. Refused up front (before anything is written)
    /// when the parts alone exceed <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<(string Stored, long Bytes, string Sha256)> StoreAsync(
        ICaptureFileStore files, string kind, IReadOnlyList<ComputeJobPart> parts, long maxBytes, CancellationToken ct)
    {
        if (parts.Sum(PartBytes) > maxBytes)
        {
            throw new CaptureFailedException($"the files to send to a 3D runner are larger than {maxBytes / (1024 * 1024)} MB.");
        }

        var stored = await files.SaveWithAsync(".zip", (output, token) => WriteAsync(output, kind, parts, token), ct);
        var path = files.ResolvePhysicalPath(stored) ?? throw new IOException("The runner envelope was not stored.");
        var size = new FileInfo(path).Length;
        if (size > maxBytes)
        {
            files.Delete(stored);
            throw new CaptureFailedException($"the files ({size / (1024 * 1024)} MB) are too large to send to a 3D runner.");
        }

        return (stored, size, await RunnerBundle.Sha256Async(path, ct));
    }

    private static bool IsInlineJson(ComputeJobPart part) => part.SourcePath is null && part.ContentType == "application/json";

    private static JsonNode? ParseJson(ComputeJobPart part)
    {
        try
        {
            return JsonNode.Parse(part.Content);
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"The part {part.Name} is not JSON.");
        }
    }

    private static string EntryName(ComputeJobPart part)
    {
        var name = part.FileName;
        if (string.IsNullOrEmpty(name) || name.Length > MaxFileNameLength || name is "." or ".." || name.Any(c => c is '/' or '\\' || char.IsControl(c)))
        {
            throw new InvalidDataException($"The part {part.Name} has no plain file name.");
        }

        return name;
    }

    private static long PartBytes(ComputeJobPart part) => part.SourcePath is { } path ? new FileInfo(path).Length : part.Content.LongLength;

    /// <summary>Stores <paramref name="source"/> (disposed here) as an uncompressed entry.</summary>
    private static async Task WriteEntryAsync(ZipArchive zip, string name, Stream source, CancellationToken ct)
    {
        await using var owned = source;
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var target = entry.Open();
        await source.CopyToAsync(target, ct);
    }
}
