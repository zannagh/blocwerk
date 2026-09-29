// <copyright file="CaptureManyPhotosTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Captures beyond the old 60-photo limit: every step gets every photo (no subset), and the big requests stream
/// the photos from disk (<see cref="ComputePhotoParts"/>) instead of holding them all in memory.
/// </summary>
public class CaptureManyPhotosTests
{
    private const int Photos = 145;

    [Fact]
    public async Task A145PhotoCapture_SendsEveryPhoto_ToTheSolveTheTexturesAndTheSplat()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var names = Enumerable.Range(1, Photos).Select(CaptureComputeDocuments.PhotoName).ToArray();
        s.Client.GeometryJson = CaptureScenario.GeometryWithCameras(names);
        var captureId = await s.StartCaptureAsync(photos: Photos);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var solve = Assert.Single(s.Client.JsonSubmissions, j => j.Kind == "solve");
        Assert.Equal(Photos, JsonNode.Parse(solve.Json)!["photos"]!.AsArray().Count);
        var (kind, parts) = Assert.Single(s.Client.MultipartSubmissions);
        Assert.Equal("textures", kind);
        var photos = parts.Where(p => p.Name == "photos").ToList();
        Assert.Equal(names.Select(n => n + ".jpg"), photos.Select(p => p.FileName));

        // Streamed from the store (the upload stored them stripped): no copy, nothing in memory, no metadata.
        Assert.All(photos, p =>
        {
            Assert.True(p.SourcePath is { } path && File.Exists(path));
            Assert.NotEmpty(p.Content);
            Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(p.Content), StringComparison.Ordinal);
        });
        var (_, splat) = Assert.Single(s.SplatClient.MultipartSubmissions);
        Assert.Equal(Photos, splat.Count(p => p.Name == "photos"));

        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
    }

    [Fact]
    public async Task PhotoParts_StreamACleanStoredPhoto_AndSpoolAStrippedCopyOfAnyOther()
    {
        var root = Path.Combine(Path.GetTempPath(), "blocwerk-photo-parts-tests", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = root;
        var files = new FileSystemCaptureFileStore(settings);
        try
        {
            var cleanName = await files.SaveAsync(ImageMetadataStripper.Strip(CaptureScenario.TinyJpeg(1)), ".jpg", CancellationToken.None);
            var withExif = ExifJpeg.Build(CaptureScenario.TinyJpeg(2));
            var dirtyName = await files.SaveAsync(withExif, ".jpg", CancellationToken.None);
            string spooled;
            using (var parts = new ComputePhotoParts(files))
            {
                var clean = await parts.PartAsync("photos", "p01", cleanName, asJpeg: false, CancellationToken.None);
                var dirty = await parts.PartAsync("photos", "p02", dirtyName, asJpeg: false, CancellationToken.None);
                var frame = await parts.PartAsync("photos", "vf_0001", cleanName, asJpeg: true, CancellationToken.None);

                Assert.Null(await parts.PartAsync("photos", "p03", "missing.jpg", asJpeg: false, CancellationToken.None));
                Assert.Equal(files.ResolvePhysicalPath(cleanName), clean!.SourcePath);
                Assert.Empty(clean.Content);
                Assert.Equal(("p01.jpg", "image/jpeg"), (clean.FileName, clean.ContentType));
                Assert.Equal(("vf_0001.jpg", "image/jpeg"), (frame!.FileName, frame.ContentType));
                spooled = dirty!.SourcePath!;
                Assert.NotEqual(files.ResolvePhysicalPath(dirtyName), spooled);
                Assert.Equal(ImageMetadataStripper.Strip(withExif), await File.ReadAllBytesAsync(spooled));
                Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(await File.ReadAllBytesAsync(spooled)), StringComparison.Ordinal);
                Assert.Equal(1, parts.Spooled);
            }

            Assert.False(File.Exists(spooled)); // the stripped copy is gone once submitted
            Assert.True(File.Exists(files.ResolvePhysicalPath(dirtyName))); // the stored photos stay
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
