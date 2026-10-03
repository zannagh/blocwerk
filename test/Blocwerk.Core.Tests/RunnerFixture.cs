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
    /// <summary>Columns of <see cref="SlimPly"/>.</summary>
    public const int SlimColumns = 14;

    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-runner-tests", Guid.NewGuid().ToString("N"));

    private RunnerFixture(WallTestHarness harness, GpuRunnerOptions? options)
    {
        Harness = harness;
        Clock = new MutableTestClock(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        var settings = new Configuration.BlocwerkSettings();
        settings.WallImage.StoragePath = storeDir;
        Files = new FileSystemCaptureFileStore(settings);
        Options = options ?? new GpuRunnerOptions { ClaimWait = TimeSpan.Zero };
        Queue = new GpuJobQueue(
            harness.RootContextFactory, Files, CaptureQueue, Options, new GpuJobSignal(), NullLogger<GpuJobQueue>.Instance, clock: Clock, diskSpace: Disk);
    }

    public WallTestHarness Harness { get; }

    public MutableTestClock Clock { get; }

    public ICaptureFileStore Files { get; }

    /// <summary>The capture store's free space as the queue sees it (unlimited until a test sets it).</summary>
    public FakeDiskSpace Disk { get; } = new();

    public WallCaptureQueue CaptureQueue { get; } = new();

    public GpuRunnerOptions Options { get; }

    public GpuJobQueue Queue { get; }

    /// <summary>A fixture over the harness's seeded wall (and its owner).</summary>
    public static async Task<RunnerFixture> CreateAsync(WallTestHarness harness, GpuRunnerOptions? options = null)
    {
        await harness.SeedWallAsync(holdCount: 0);
        return new RunnerFixture(harness, options);
    }

    /// <summary>A minimal valid 3DGS PLY (the slim columns the runner uploads) with <paramref name="count"/> splats.</summary>
    public static byte[] SlimPly(int count = 3) => [.. PlyHeader(count), .. new byte[count * SlimColumns * 4]];

    /// <summary>The header of <see cref="SlimPly"/> for <paramref name="count"/> splats.</summary>
    public static byte[] PlyHeader(int count)
    {
        string[] cols = ["x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"];
        var header = new StringBuilder("ply\nformat binary_little_endian 1.0\n")
            .Append($"element vertex {count}\n");
        foreach (var c in cols)
        {
            header.Append($"property float {c}\n");
        }

        header.Append("end_header\n");
        return Encoding.ASCII.GetBytes(header.ToString());
    }

    /// <summary><paramref name="bytes"/> gzip-compressed (a runner's <c>Content-Encoding: gzip</c> upload).</summary>
    public static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
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

    /// <summary>A runner of the harness owner (or <paramref name="ownerId"/>), serving <paramref name="walls"/>.</summary>
    public async Task<(GpuRunner Runner, string Token)> AddRunnerAsync(
        string name, bool shared = false, bool online = true, string? maxQuality = "max", Guid? ownerId = null, params Guid[] walls)
    {
        var (token, prefix) = GpuRunnerTokens.Create();
        var runner = new GpuRunner
        {
            Name = name, OwnerUserId = ownerId ?? Harness.Owner.Id, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix,
            SharedWithOtherWalls = shared, LastSeenAt = online ? Clock.GetUtcNow() : null, MaxQuality = maxQuality,
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

    /// <summary>A wall of the harness owner (or of a new user when <paramref name="foreign"/>).</summary>
    public async Task<Guid> AddWallAsync(string name, bool foreign = false)
    {
        await using var db = Harness.CreateContext();
        var owner = Harness.Owner.Id;
        if (foreign)
        {
            var user = new User { Identifier = $"other-{Guid.NewGuid():N}", DisplayName = "Other admin" };
            db.Users.Add(user);
            owner = user.Id;
        }

        var wall = new Wall { Name = name, OwnerId = owner };
        db.Walls.Add(wall);
        await db.SaveChangesAsync();
        return wall.Id;
    }

    /// <summary>The wall's admin (the harness owner, or <paramref name="approverId"/>) approves one shared runner.</summary>
    public async Task ApproveAsync(Guid wallId, GpuRunner runner, Guid? approverId = null)
    {
        await using var db = Harness.CreateContext();
        db.GpuRunnerApprovals.Add(new GpuRunnerApproval { WallId = wallId, RunnerId = runner.Id, ApprovedByUserId = approverId ?? Harness.Owner.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>A finished capture ("Model ready") on <paramref name="wallId"/> whose photo-real view waits in a queued job.</summary>
    public async Task<GpuJob> AddJobAsync(Guid wallId, SplatQuality quality = SplatQuality.Draft)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        await using var db = Harness.CreateContext();
        var capture = new WallCapture
        {
            WallId = wallId, CreatedByUserId = Harness.Owner.Id, Status = WallCaptureStatus.Succeeded, Stage = "Done",
        };
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        var bundle = RunnerBundle.Sanitize(Bundle());
        var job = new GpuJob
        {
            WallId = wallId, CaptureId = capture.Id, GeometryModelId = Guid.NewGuid(), Quality = quality,
            BundlePath = await Files.SaveAsync(bundle.Bytes, ".zip", CancellationToken.None), BundleBytes = bundle.Bytes.Length,
            BundleSha256 = bundle.Sha256, PreparedPath = await Files.SaveAsync("{}"u8.ToArray(), ".prep", CancellationToken.None),
        };
        return await Queue.EnqueueAsync(job, CancellationToken.None);
    }

    /// <summary>Marks the runner as seen at <paramref name="at"/>.</summary>
    public async Task MarkOnlineAsync(GpuRunner runner, DateTimeOffset at)
    {
        await using var db = Harness.CreateContext();
        var row = await db.GpuRunners.FindAsync(runner.Id);
        row!.LastSeenAt = at;
        await db.SaveChangesAsync();
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

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }
}
