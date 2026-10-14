// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The bundle of a hold detection job (<see cref="Entities.GpuJobKind.DetectHolds"/>), a zip the runner downloads like a
/// training bundle: <c>holds-job.json</c> (the format version and the app's detector settings: tiling, confidences, the
/// window NMS overlap), <c>model.onnx</c> (the app's own detector model, so the runner finds exactly what the app would) and
/// <c>photos/&lt;camera name&gt;.jpg|png</c> (the photos the search reads, each metadata-stripped like every photo that
/// leaves the server). Built photo by photo from the store: only one image is ever in memory.
/// </summary>
public static class RunnerHoldsBundle
{
    public const string JobFile = "holds-job.json";
    public const string ModelFile = "model.onnx";
    public const string PhotoDir = "photos/";

    /// <summary>The app's whole-image confidence when tiling is off (<c>YoloHoldDetectionService</c>).</summary>
    public const double WholeImageConfidence = 0.25;

    /// <summary>YoloDotNet's IoU for the per-window NMS (<c>YoloTiledDetector.WindowIoU</c>).</summary>
    public const double WindowIoU = 0.45;

    /// <summary>The job document the runner reads (its detector replays the app's, see <c>gpurunner/holds.py</c>).</summary>
    public static string JobDocument(HoldDetectionTilingSettings tiling) =>
        new JsonObject
        {
            ["version"] = 1,
            ["tiling"] = new JsonObject
            {
                ["enabled"] = tiling.Enabled,
                ["tileSize"] = tiling.TileSize,
                ["overlap"] = tiling.Overlap,
                ["confidence"] = tiling.Confidence,
                ["sampling"] = tiling.Sampling,
            },
            ["wholeImageConfidence"] = WholeImageConfidence,
            ["windowIoU"] = WindowIoU,
        }.ToJsonString();

    /// <summary>Whether the runner's detector can replay these settings (it samples windows the app's default way only).</summary>
    public static bool Replayable(HoldDetectionTilingSettings tiling) =>
        !tiling.Enabled || string.Equals(tiling.Sampling?.Trim(), "nearest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the bundle into the store; returns its stored name, size, SHA-256 and the photos in it. A photo missing from the
    /// store is skipped (the search detects it in the app if it can); no photo at all throws <see cref="CaptureFailedException"/>.
    /// Each photo is its camera name (the name the search knows it by) and its name in the capture store.
    /// </summary>
    public static async Task<(string Stored, long Bytes, string Sha256, int Photos)> StoreAsync(
        ICaptureFileStore files, string modelPath, HoldDetectionTilingSettings tiling, IReadOnlyList<(string Name, string StoredPath)> photos,
        long maxBytes, CancellationToken ct)
    {
        var count = 0;
        var stored = await files.SaveWithAsync(
            ".zip",
            async (output, token) =>
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                Write(zip, JobFile, Encoding.UTF8.GetBytes(JobDocument(tiling)), CompressionLevel.Optimal);
                await WriteFileAsync(zip, ModelFile, modelPath, token);
                count = await WritePhotosAsync(zip, files, photos, token);
            },
            ct);
        var path = files.ResolvePhysicalPath(stored) ?? throw new IOException("The hold detection bundle was not stored.");
        var size = new FileInfo(path).Length;
        if (size > maxBytes)
        {
            files.Delete(stored);
            throw new CaptureFailedException(string.Create(
                CultureInfo.InvariantCulture, $"the photos ({size / (1024 * 1024)} MB) are too large to send to a 3D runner."));
        }

        return (stored, size, await RunnerBundle.Sha256Async(path, ct), count);
    }

    private static async Task<int> WritePhotosAsync(
        ZipArchive zip, ICaptureFileStore files, IReadOnlyList<(string Name, string StoredPath)> photos, CancellationToken ct)
    {
        var count = 0;
        foreach (var photo in photos)
        {
            if (await files.ReadAsync(photo.StoredPath, ct) is not { } bytes)
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
            throw new CaptureFailedException("None of the capture photos is on the server any more.");
        }

        return count;
    }

    private static async Task WriteFileAsync(ZipArchive zip, string name, string path, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var target = entry.Open();
        await using var source = File.OpenRead(path);
        await source.CopyToAsync(target, ct);
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
