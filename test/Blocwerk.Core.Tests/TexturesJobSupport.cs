// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Builders shared by the textures-on-a-runner tests: runners that advertise <c>textures</c>, queued textures jobs, result zips.</summary>
internal static class TexturesJobSupport
{
    /// <summary>The hello of a runner that renders textures with <paramref name="memoryMb"/> (and trains splats).</summary>
    public static RunnerHello Hello(int memoryMb = 20000, params string[] capabilities) =>
        new("1.0", "Apple M4", 49152, "max", 40000, "Darwin arm64", null, "brush", false, false,
            capabilities.Length == 0 ? [RunnerCapabilities.Splat, RunnerCapabilities.Textures] : capabilities, memoryMb);

    /// <summary>A runner of the harness owner serving its wall that said hello with the textures capability.</summary>
    public static async Task<GpuRunner> AddTexturesRunnerAsync(WallTestHarness h, GpuJobQueue queue, string name = "Mac", int memoryMb = 20000)
    {
        var (token, prefix) = GpuRunnerTokens.Create();
        var runner = new GpuRunner
        {
            Name = name, OwnerUserId = h.Owner.Id, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix, LastSeenAt = DateTimeOffset.UtcNow,
        };
        await using (var db = h.CreateContext())
        {
            db.GpuRunners.Add(runner);
            db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = h.WallId });
            await db.SaveChangesAsync();
        }

        await queue.HelloAsync(runner, Hello(memoryMb), CancellationToken.None);
        return runner;
    }

    /// <summary>A queued textures job of a new finished capture of <paramref name="wallId"/>, bundled like the pipeline does.</summary>
    public static async Task<GpuJob> AddTexturesJobAsync(RunnerFixture f, Guid wallId, int requiredMb = 1000)
    {
        await using var db = f.Harness.CreateContext();
        var capture = new WallCapture { WallId = wallId, CreatedByUserId = f.Harness.Owner.Id, Status = WallCaptureStatus.Succeeded, Stage = "Done" };
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        var photo = await f.Files.SaveAsync(CaptureScenario.TinyJpeg(), ".jpg", CancellationToken.None);
        var bundle = await RunnerTexturesBundle.StoreAsync(
            f.Files, CaptureScenario.GeometryWithCameras("photo_0000"), "{\"blendViews\":6}", [new RunnerTexturesBundle.Photo("photo_0000", photo)],
            long.MaxValue, CancellationToken.None);
        return await f.Queue.EnqueueAsync(
            new GpuJob
            {
                WallId = wallId, CaptureId = capture.Id, GeometryModelId = Guid.NewGuid(), Kind = GpuJobKind.Textures, Quality = SplatQuality.Draft,
                RequiredMemoryMb = requiredMb, BundlePath = bundle.Stored, BundleBytes = bundle.Bytes, BundleSha256 = bundle.Sha256,
                PreparedPath = bundle.Stored,
            },
            CancellationToken.None);
    }

    /// <summary>The result a runner uploads: the wall-geometry service's output files (one facet with mask and source map) in a zip.</summary>
    public static byte[] ResultZip(bool withFiles = true, params (string Name, byte[] Bytes)[] extra)
    {
        var manifest = new JsonObject
        {
            ["facets"] = new JsonArray(new JsonObject
            {
                ["facet"] = "0", ["file"] = "facet_0.jpg", ["maskFile"] = "facet_0_mask.png", ["sourceFile"] = "facet_0_source.json",
                ["widthPx"] = 1550, ["heightPx"] = 1300,
                ["bounds"] = new JsonObject { ["aMin"] = -100.0, ["aMax"] = 3000.0, ["bMin"] = -100.0, ["bMax"] = 2500.0 },
            }),
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, RunnerTexturesResult.ManifestName, Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            if (withFiles)
            {
                Add(zip, "facet_0.jpg", CaptureScenario.TinyJpeg());
                Add(zip, "facet_0_mask.png", FakeComputeJobClient.MaskPng);
                Add(zip, "facet_0_source.json", FakeComputeJobClient.SourceMap);
            }

            foreach (var (name, bytes) in extra)
            {
                Add(zip, name, bytes);
            }
        }

        return output.ToArray();
    }

    /// <summary>The job row as stored now.</summary>
    public static async Task<GpuJob> JobAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }
}
