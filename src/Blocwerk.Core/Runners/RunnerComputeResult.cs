// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Runners;

/// <summary>
/// What a runner uploads for a <see cref="Entities.GpuJobKind.SplatPrepare"/> / <see cref="Entities.GpuJobKind.SplatFinish"/>
/// job: a flat zip of <c>status.json</c> (exactly the body of the splat worker's <c>GET /v1/jobs/{id}</c>, status
/// <c>succeeded</c>) and every file its <c>result.files</c> lists, stored uncompressed (<c>bundle.zip</c>, <c>prepared.json</c>,
/// <c>sparse.zip</c>, <c>wall.splat</c>, <c>wall.spz</c>, <c>frame.json</c> …). The server reads nothing else: a path, a file
/// the status does not list, a listed file that is missing, a compressed file or a zip that inflates past the cap refuses the
/// upload. The files are only ever served from the zip by name.
/// </summary>
public static partial class RunnerComputeResult
{
    /// <summary>The status entry of the zip.</summary>
    public const string StatusName = "status.json";

    /// <summary>The <see cref="Entities.GpuJob.ResultFormat"/> of a splat-prepare / splat-finish result.</summary>
    public const string Format = "zip";

    private const int MaxEntries = 64;
    private const long MaxStatusBytes = 4L * 1024 * 1024;
    private static readonly string[] Extensions = [".zip", ".json", ".splat", ".spz", ".ply"];

    /// <summary>The server's check of an uploaded zip; throws <see cref="InvalidDataException"/> when it is not such a result.</summary>
    public static void Validate(string path, long maxBytes)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is 0 or > MaxEntries)
        {
            throw new InvalidDataException("The result has no files or too many.");
        }

        CheckEntries(zip, maxBytes);
        var status = ReadStatus(zip);
        if (status.Status != ComputeJobStates.Succeeded)
        {
            throw new InvalidDataException("The result's status is not succeeded.");
        }

        var listed = ListedFiles(status);
        if (listed.Count == 0)
        {
            throw new InvalidDataException("The result lists no files.");
        }

        if (listed.FirstOrDefault(n => n == StatusName || zip.GetEntry(n) is null) is { } missing)
        {
            throw new InvalidDataException($"The result lacks {missing}, which its status names.");
        }

        if (zip.Entries.FirstOrDefault(e => e.FullName != StatusName && !listed.Contains(e.FullName)) is { } extra)
        {
            throw new InvalidDataException($"The result has a file its status does not name: {extra.FullName}");
        }
    }

    /// <summary>The status of the stored zip as the splat worker would have answered it (so the normal install reads it).</summary>
    public static ComputeJobStatus ToStatus(string path, string jobId)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadStatus(zip) with { JobId = jobId };
    }

    private static void CheckEntries(ZipArchive zip, long maxBytes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!SafeName().IsMatch(entry.FullName) || !Extensions.Contains(Path.GetExtension(entry.FullName).ToLowerInvariant())
                || !seen.Add(entry.FullName))
            {
                throw new InvalidDataException($"The result has an unexpected file: {entry.FullName}");
            }

            // Stored files cannot inflate: their size is what they cost on the wire. Only the small status may be compressed.
            if (entry.FullName != StatusName && entry.CompressedLength != entry.Length)
            {
                throw new InvalidDataException($"The result's file {entry.FullName} is compressed.");
            }

            total += entry.Length;
            if (total > maxBytes)
            {
                throw new InvalidDataException("The result is too large.");
            }
        }
    }

    private static ComputeJobStatus ReadStatus(ZipArchive zip)
    {
        var entry = zip.GetEntry(StatusName) ?? throw new InvalidDataException("The result has no status.json.");
        if (entry.Length > MaxStatusBytes)
        {
            throw new InvalidDataException("The status is too large.");
        }

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();

            // A header that lies about the size must not make the status bigger than the cap: stop at the first byte past it.
            var chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > entry.Length)
                {
                    throw new InvalidDataException("The status is larger than its header says.");
                }
            }

            buffer.Position = 0;
            return JsonSerializer.Deserialize<ComputeJobStatus>(buffer) ?? throw new InvalidDataException("The status is empty.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The status is not JSON.");
        }
    }

    private static HashSet<string> ListedFiles(ComputeJobStatus status)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (status.Result is { ValueKind: JsonValueKind.Object } result && result.TryGetProperty("files", out var files)
            && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                var name = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("name", out var n) ? n : file;
                if (name.ValueKind != JsonValueKind.String || !SafeName().IsMatch(name.GetString()!))
                {
                    throw new InvalidDataException("The status lists a file with an unexpected name.");
                }

                names.Add(name.GetString()!);
            }
        }

        return names;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,127}$")]
    private static partial Regex SafeName();
}
