// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The input of a <see cref="Entities.GpuJobKind.SplatPrepare"/> / <see cref="Entities.GpuJobKind.SplatFinish"/> job: the
/// multipart submission the splat worker would have received, as one zip a 3D runner downloads like any bundle. The zip holds
/// <c>parts.json</c> (<c>{"version":1,"service":"splat","kind":"splat-prepare","parts":[{"name":…,"file":…}|{"name":…,"json":…}]}</c>)
/// and one stored (uncompressed) entry per file part, named exactly like the part's file name. A JSON field travels inline
/// as <c>json</c>. The runner rebuilds the request from it and runs the same job function the HTTP worker runs; what it
/// uploads is read back by <see cref="RunnerComputeResult"/>. Written part by part from the store: no file is held in memory,
/// and a JSON field is copied through, not parsed.
/// </summary>
public static class RunnerComputeEnvelope
{
    /// <summary>The manifest entry of the zip.</summary>
    public const string ManifestName = "parts.json";

    /// <summary>The envelope format version.</summary>
    public const int Version = 1;

    /// <summary>The compute service the envelope is for.</summary>
    public const string Service = "splat";

    /// <summary>The most entries an envelope holds (the manifest and its files); a runner refuses more.</summary>
    public const int MaxEntries = CaptureLimits.MaxPhotosCeiling + CaptureLimits.MaxVideoFramesCeiling + CaptureLimits.AnchorImages + 16;

    private const int MaxFileNameLength = 255;

    /// <summary>
    /// Streams the envelope into <paramref name="output"/>: the manifest first, then the file parts in order. A file part without
    /// a plain file name, a name taken twice (ignoring case), more than <see cref="MaxEntries"/> entries, or a JSON part that is
    /// not JSON throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task WriteAsync(Stream output, string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct)
    {
        var files = parts.Where(p => !IsInlineJson(p)).Select(p => (Part: p, Name: EntryName(p))).ToList();
        CheckEntries(files.Select(f => f.Name));
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteManifestAsync(zip, kind, parts, ct);
        foreach (var (part, name) in files)
        {
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

    private static void CheckEntries(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        foreach (var name in names)
        {
            if (!seen.Add(name))
            {
                throw new InvalidDataException($"Two parts of the envelope are named {name}.");
            }
        }

        if (seen.Count > MaxEntries)
        {
            throw new InvalidDataException($"The envelope would hold more than {MaxEntries} entries.");
        }
    }

    /// <summary>
    /// Writes the manifest straight into its entry: an inline JSON part (<c>prepared</c> can be tens of MB) is validated and copied
    /// as it is, never parsed into a document.
    /// </summary>
    private static async Task WriteManifestAsync(ZipArchive zip, string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct)
    {
        await using var target = NewEntry(zip, ManifestName).Open();
        await using var json = new Utf8JsonWriter(target);
        json.WriteStartObject();
        json.WriteNumber("version", Version);
        json.WriteString("service", Service);
        json.WriteString("kind", kind);
        json.WriteStartArray("parts");
        foreach (var part in parts)
        {
            json.WriteStartObject();
            json.WriteString("name", part.Name);
            if (IsInlineJson(part))
            {
                json.WritePropertyName("json");
                WriteRawJson(json, part);
            }
            else
            {
                json.WriteString("file", EntryName(part));
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    private static void WriteRawJson(Utf8JsonWriter json, ComputeJobPart part)
    {
        try
        {
            json.WriteRawValue(part.Content, skipInputValidation: false);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"The part {part.Name} is not JSON.");
        }
    }

    private static string EntryName(ComputeJobPart part)
    {
        var name = part.FileName;
        if (string.IsNullOrEmpty(name) || name.Length > MaxFileNameLength || name is "." or ".." || name.Any(c => c is '/' or '\\' or ':' || char.IsControl(c)))
        {
            throw new InvalidDataException($"The part {part.Name} has no plain file name.");
        }

        return name;
    }

    private static long PartBytes(ComputeJobPart part) => part.SourcePath is { } path ? new FileInfo(path).Length : part.Content.LongLength;

    private static ZipArchiveEntry NewEntry(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }

    /// <summary>Stores <paramref name="source"/> (disposed here) as an uncompressed entry.</summary>
    private static async Task WriteEntryAsync(ZipArchive zip, string name, Stream source, CancellationToken ct)
    {
        await using var owned = source;
        await using var target = NewEntry(zip, name).Open();
        await source.CopyToAsync(target, ct);
    }
}
