// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The bundle of a textures job (<see cref="Entities.GpuJobKind.Textures"/>), a zip the runner downloads like a training bundle:
/// <c>textures-job.json</c> (<c>{"version":1,"options":{…}}</c>, the same <c>options</c> the wall-geometry service takes),
/// <c>geometry.json</c> (the solved model's document, carried-over facets left out) and <c>photos/&lt;camera name&gt;.jpg|png</c>
/// (only the photos the model solved, each metadata-stripped like every photo that leaves the server).
/// Built photo by photo from the store: only one image is ever in memory.
/// </summary>
public static class RunnerTexturesBundle
{
    public const string JobFile = "textures-job.json";
    public const string GeometryFile = "geometry.json";
    public const string PhotoDir = "photos/";

    /// <summary>One photo of the bundle: the camera name the geometry knows it by and its name in the capture store.</summary>
    public sealed record Photo(string Name, string StoredPath);

    /// <summary>The job document: the format version and the renderer's options (null: the renderer's defaults).</summary>
    public static string JobDocument(string? optionsJson) =>
        new JsonObject { ["version"] = 1, ["options"] = optionsJson is null ? null : JsonNode.Parse(optionsJson) }.ToJsonString();

    /// <summary>
    /// Writes the bundle into the store; returns its stored name, size and SHA-256. A photo missing from the store is skipped
    /// (the renderer ignores cameras without a photo); no photo at all throws <see cref="CaptureFailedException"/>.
    /// </summary>
    public static async Task<(string Stored, long Bytes, string Sha256)> StoreAsync(
        ICaptureFileStore files, string geometryJson, string? optionsJson, IReadOnlyList<Photo> photos, long maxBytes, CancellationToken ct)
    {
        var stored = await files.SaveWithAsync(
            ".zip",
            async (output, token) =>
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                Write(zip, JobFile, Encoding.UTF8.GetBytes(JobDocument(optionsJson)), CompressionLevel.Optimal);
                Write(zip, GeometryFile, Encoding.UTF8.GetBytes(geometryJson), CompressionLevel.Optimal);
                var count = 0;
                foreach (var photo in photos)
                {
                    if (await files.ReadAsync(photo.StoredPath, token) is not { } bytes)
                    {
                        continue;
                    }

                    var clean = ImageMetadataStripper.Strip(bytes);
                    var name = PhotoDir + photo.Name + CapturePhotoFormat.Extension(CapturePhotoFormat.Sniff(clean));
                    Write(zip, name, clean, CompressionLevel.NoCompression);
                    count++;
                }

                if (count == 0)
                {
                    throw new CaptureFailedException("None of the photos the 3D model used is on the server any more.");
                }
            },
            ct);
        var path = files.ResolvePhysicalPath(stored) ?? throw new IOException("The textures bundle was not stored.");
        var size = new FileInfo(path).Length;
        if (size > maxBytes)
        {
            files.Delete(stored);
            throw new CaptureFailedException($"the photos ({size / (1024 * 1024)} MB) are too large to send to a 3D runner.");
        }

        return (stored, size, await RunnerBundle.Sha256Async(path, ct));
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
