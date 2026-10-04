// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Runners;

/// <summary>
/// What a runner uploads for a textures job: a flat zip of exactly what the wall-geometry service's <c>textures</c> job
/// produces: <c>textures.json</c> (the manifest the app parses with <see cref="CaptureComputeDocuments.ParseTextureResult"/>) and
/// every file it names (<c>facet_*.jpg</c>, <c>facet_*_mask.png</c>, <c>facet_*_source.json</c>). The server only reads
/// names it knows; a path, a stray file or a manifest entry without its file refuses the upload.
/// </summary>
public static partial class RunnerTexturesResult
{
    /// <summary>The manifest entry of the zip.</summary>
    public const string ManifestName = "textures.json";

    /// <summary>The <see cref="Entities.GpuJob.ResultFormat"/> of a textures job's result.</summary>
    public const string Format = "zip";

    private const int MaxEntries = 4 * 64 + 1;
    private const long MaxManifestBytes = 4L * 1024 * 1024;

    /// <summary>The server's check of an uploaded zip; throws <see cref="InvalidDataException"/> when it is not a textures result.</summary>
    public static void Validate(string path, long maxUncompressedBytes)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is 0 or > MaxEntries)
        {
            throw new InvalidDataException("The textures result has no files or too many.");
        }

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!SafeName().IsMatch(entry.FullName))
            {
                throw new InvalidDataException($"The textures result has an unexpected file: {entry.FullName}");
            }

            total += entry.Length;
        }

        if (total > maxUncompressedBytes)
        {
            throw new InvalidDataException("The textures result is too large.");
        }

        var manifest = ReadManifest(zip);
        foreach (var entry in CaptureComputeDocuments.ParseTextureResult(manifest))
        {
            foreach (var name in new[] { entry.File, entry.MaskFile, entry.SourceFile }.OfType<string>())
            {
                if (zip.GetEntry(name) is null)
                {
                    throw new InvalidDataException($"The textures result lacks {name}, which its manifest names.");
                }
            }
        }

        if (CaptureComputeDocuments.ParseTextureResult(manifest).Count == 0)
        {
            throw new InvalidDataException("The textures result has no textures.");
        }
    }

    /// <summary>The manifest of the stored zip as the status a compute worker would have answered (so the normal install reads it).</summary>
    public static ComputeJobStatus ToStatus(string path, string jobId)
    {
        using var zip = ZipFile.OpenRead(path);
        return new ComputeJobStatus { JobId = jobId, Status = ComputeJobStates.Succeeded, Progress = 1, Result = ReadManifest(zip) };
    }

    private static JsonElement ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("The textures result has no textures.json.");
        if (entry.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("The textures manifest is too large.");
        }

        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The textures manifest is not JSON.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,128}\.(jpg|jpeg|png|json)$")]
    private static partial Regex SafeName();
}
