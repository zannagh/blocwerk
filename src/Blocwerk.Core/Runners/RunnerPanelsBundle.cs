// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The bundle of a panel registration job (<see cref="Entities.GpuJobKind.RegisterPanels"/>), a zip the runner downloads
/// like a training bundle (<c>gpurunner/panels.py</c>): <c>register-job.json</c> (the capture's solved cameras, world mm, and
/// the panel names), <c>photos/&lt;camera&gt;.jpg</c> (the capture photos) and <c>panels/&lt;panel id&gt;.jpg</c>, metadata
/// stripped so COLMAP reads the raw pixel grid the cameras were solved on.
/// </summary>
public static class RunnerPanelsBundle
{
    public const string JobFile = "register-job.json";
    public const string PhotoDir = "photos/";
    public const string PanelDir = "panels/";

    /// <summary>
    /// The panel photo's name in the bundle and the result: the panel id (32 hex digits), "r", its photo revision. A crop
    /// rewrites the photo in place and moves the revision, so a pose of the old photo is never taken for the new one.
    /// </summary>
    public static string Name(Guid panelId, int photoRevision) => string.Create(CultureInfo.InvariantCulture, $"{panelId:N}r{photoRevision}");

    /// <summary>The job document: every camera that has a photo in the bundle, and the panel names.</summary>
    public static string JobDocument(IEnumerable<SolvedCamera> cameras, IEnumerable<string> panels) =>
        new JsonObject
        {
            ["version"] = 1,
            ["cameras"] = new JsonArray(cameras.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Image,
                ["width"] = c.Width,
                ["height"] = c.Height,
                ["K"] = Numbers(c.K),
                ["dist"] = Numbers(c.Dist),
                ["R"] = Numbers(c.R),
                ["t"] = Numbers(c.T),
            }).ToArray()),
            ["panels"] = new JsonArray(panels.Select(p => (JsonNode)p).ToArray()),
        }.ToJsonString();

    /// <summary>
    /// Writes the bundle into the store; returns its stored name, size and SHA-256. Capture photos missing from the store are
    /// left out with their cameras; fewer than two posed photos throws <see cref="CaptureFailedException"/>.
    /// </summary>
    public static async Task<(string Stored, long Bytes, string Sha256)> StoreAsync(
        ICaptureFileStore files,
        IReadOnlyList<(SolvedCamera Camera, string StoredPath)> photos,
        IReadOnlyList<(Guid PanelId, int Revision, byte[] Photo)> panels,
        long maxBytes,
        CancellationToken ct)
    {
        var stored = await files.SaveWithAsync(".zip", (output, token) => WriteAsync(output, files, photos, panels, token), ct);
        var path = files.ResolvePhysicalPath(stored) ?? throw new IOException("The panel registration bundle was not stored.");
        var size = new FileInfo(path).Length;
        if (size > maxBytes)
        {
            files.Delete(stored);
            throw new CaptureFailedException(string.Create(
                CultureInfo.InvariantCulture, $"the photos ({size / (1024 * 1024)} MB) are too large to send to a 3D runner."));
        }

        return (stored, size, await RunnerBundle.Sha256Async(path, ct));
    }

    private static async Task WriteAsync(
        Stream output,
        ICaptureFileStore files,
        IReadOnlyList<(SolvedCamera Camera, string StoredPath)> photos,
        IReadOnlyList<(Guid PanelId, int Revision, byte[] Photo)> panels,
        CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var posed = new List<SolvedCamera>();
        foreach (var (camera, storedPath) in photos)
        {
            if (await files.ReadAsync(storedPath, ct) is not { } bytes)
            {
                continue;
            }

            var clean = ImageMetadataStripper.Strip(bytes);
            Write(zip, PhotoDir + camera.Image + Extension(clean), clean);
            posed.Add(camera);
        }

        if (posed.Count < 2)
        {
            throw new CaptureFailedException("The capture photos are not on the server any more.");
        }

        foreach (var (panelId, revision, photo) in panels)
        {
            var clean = ImageMetadataStripper.Strip(photo);
            Write(zip, PanelDir + Name(panelId, revision) + Extension(clean), clean);
        }

        Write(zip, JobFile, Encoding.UTF8.GetBytes(JobDocument(posed, panels.Select(p => Name(p.PanelId, p.Revision)))));
    }

    private static string Extension(byte[] bytes) => CapturePhotoFormat.Extension(CapturePhotoFormat.Sniff(bytes));

    private static JsonArray Numbers(IEnumerable<double> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
