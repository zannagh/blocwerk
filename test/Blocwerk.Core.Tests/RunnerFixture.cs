// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Seeds runners, walls, captures and GPU jobs for the 3D-runner tests.</summary>
internal sealed class RunnerFixture : IDisposable
{
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-runner-tests", Guid.NewGuid().ToString("N"));

    private RunnerFixture(WallTestHarness harness, GpuRunnerOptions? options)
    {
        Harness = harness;
        Clock = new MutableTestClock(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        var settings = new Configuration.BlocwerkSettings();
        settings.WallImage.StoragePath = storeDir;
        Files = new FileSystemCaptureFileStore(settings);
        Options = options ?? new GpuRunnerOptions { ClaimWait = TimeSpan.Zero };
        Queue = new GpuJobQueue(harness.RootContextFactory, Files, CaptureQueue, Options, new GpuJobSignal(), NullLogger<GpuJobQueue>.Instance, clock: Clock);
    }

    public WallTestHarness Harness { get; }

    /// <summary>A fixture over the harness's seeded wall (and its owner).</summary>
    public static async Task<RunnerFixture> CreateAsync(WallTestHarness harness, GpuRunnerOptions? options = null)
    {
        await harness.SeedWallAsync(holdCount: 0);
        return new RunnerFixture(harness, options);
    }

    public MutableTestClock Clock { get; }

    public ICaptureFileStore Files { get; }

    public WallCaptureQueue CaptureQueue { get; } = new();

    public GpuRunnerOptions Options { get; }

    public GpuJobQueue Queue { get; }

    /// <summary>A minimal valid 3DGS PLY (the slim columns the runner uploads) with <paramref name="count"/> splats.</summary>
    public static byte[] SlimPly(int count = 3)
    {
        string[] cols = ["x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"];
        var header = new StringBuilder("ply\nformat binary_little_endian 1.0\n")
            .Append($"element vertex {count}\n");
        foreach (var c in cols)
        {
            header.Append($"property float {c}\n");
        }

        header.Append("end_header\n");
        return [.. Encoding.ASCII.GetBytes(header.ToString()), .. new byte[count * cols.Length * 4]];
    }

    /// <summary>A prepare-job bundle: train.json, one image WITH EXIF/GPS, one sparse file; plus extra entries.</summary>
    public static byte[] Bundle(params (string Name, byte[] Bytes)[] extra)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "train.json", Encoding.UTF8.GetBytes("{\"version\":1,\"quality\":\"draft\"}"));
            Add(zip, "dataset/images/cam/p01.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg()));
            Add(zip, "dataset/sparse/0/cameras.bin", [1, 2, 3, 4]);
            foreach (var (name, bytes) in extra)
            {
                Add(zip, name, bytes);
            }
        }

        return output.ToArray();
    }

    public async Task<(GpuRunner Runner, string Token)> AddRunnerAsync(
        string name, bool shared = false, bool online = true, params Guid[] walls)
    {
        var (token, prefix) = GpuRunnerTokens.Create();
        var runner = new GpuRunner
        {
            Name = name, OwnerUserId = Harness.Owner.Id, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix,
            SharedWithOtherWalls = shared, LastSeenAt = online ? Clock.GetUtcNow() : null,
        };
        await using var db = Harness.CreateContext();
        db.GpuRunners.Add(runner);
        foreach (var wall in walls)
        {
            db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = wall });
        }

        await db.SaveChangesAsync();
        return (runner, token);
    }

    public async Task<Guid> AddWallAsync(string name)
    {
        await using var db = Harness.CreateContext();
        var wall = new Wall { Name = name, OwnerId = Harness.Owner.Id };
        db.Walls.Add(wall);
        await db.SaveChangesAsync();
        return wall.Id;
    }

    /// <summary>A capture waiting for a runner on <paramref name="wallId"/>, and its queued job.</summary>
    public async Task<GpuJob> AddJobAsync(Guid wallId)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        await using var db = Harness.CreateContext();
        var capture = new WallCapture
        {
            WallId = wallId, CreatedByUserId = Harness.Owner.Id, Status = WallCaptureStatus.AwaitingRunner, Stage = "waiting",
        };
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        var bundle = RunnerBundle.Sanitize(Bundle());
        var job = new GpuJob
        {
            WallId = wallId, CaptureId = capture.Id, GeometryModelId = Guid.NewGuid(), Quality = SplatQuality.Draft,
            BundlePath = await Files.SaveAsync(bundle.Bytes, ".zip", CancellationToken.None), BundleBytes = bundle.Bytes.Length,
            BundleSha256 = bundle.Sha256, PreparedPath = await Files.SaveAsync("{}"u8.ToArray(), ".prep", CancellationToken.None),
        };
        return await Queue.EnqueueAsync(job, CancellationToken.None);
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(storeDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was stored.
        }
    }
}
