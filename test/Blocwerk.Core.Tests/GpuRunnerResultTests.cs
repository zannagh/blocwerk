// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>What a runner uploads (size cap, content check) and what it downloads (a sanitised bundle).</summary>
public class GpuRunnerResultTests
{
    [Fact]
    public async Task Result_OverTheCap_IsRefused_AndNothingIsKept()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { MaxResultBytes = 1024, ClaimWait = TimeSpan.Zero });
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        var outcome = await f.Queue.AcceptResultAsync(runner, job.Id, new MemoryStream(RunnerFixture.SlimPly(100)), null, null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.TooLarge, outcome);
        await using var db = h.CreateContext();
        Assert.Equal(GpuJobStatus.Claimed, (await db.GpuJobs.SingleAsync()).Status);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name.EndsWith(".upl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not a splat at all")]
    [InlineData("ply\nformat ascii 1.0\nelement vertex 1\nproperty float x\nend_header\n1\n")]
    public async Task Result_ThatIsNotASplat_IsRefused(string body)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        var outcome = await f.Queue.AcceptResultAsync(runner, job.Id, new MemoryStream(Encoding.ASCII.GetBytes(body)), null, null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Invalid, outcome);
    }

    [Fact]
    public async Task TruncatedPly_IsRefused()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, RunnerFixture.SlimPly(10)[..^8]);
        Assert.Throws<InvalidDataException>(() => SplatResultFormat.Validate(path));
        File.Delete(path);
    }

    [Fact]
    public async Task ValidResult_CompletesTheJob_AndHandsTheCaptureBackToThePipeline()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        var outcome = await f.Queue.AcceptResultAsync(
            runner, job.Id, new MemoryStream(RunnerFixture.SlimPly()), null, "{\"steps\":5000,\"runnerGpu\":\"Apple M4\"}", CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal(GpuJobStatus.Succeeded, row.Status);
        Assert.Equal(SplatResultFormat.Ply, row.ResultFormat);
        Assert.Contains("Apple M4", row.ResultStatsJson);
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Splatting, capture.Status);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal(capture.Id, await f.CaptureQueue.DequeueAsync(cts.Token));
    }

    [Fact]
    public void Bundle_IsRebuiltWithoutAnyMetadata()
    {
        var (bytes, sha) = RunnerBundle.Sanitize(RunnerFixture.Bundle());

        Assert.Equal(64, sha.Length);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(["dataset/images/cam/p01.jpg", "dataset/sparse/0/cameras.bin", "train.json"], zip.Entries.Select(e => e.FullName).Order());
        using var image = new MemoryStream();
        zip.GetEntry("dataset/images/cam/p01.jpg")!.Open().CopyTo(image);
        var jpeg = image.ToArray();
        Assert.Equal(-1, jpeg.AsSpan().IndexOf("Exif\0\0"u8));
        Assert.Equal(-1, jpeg.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes));
        Assert.Equal(-1, jpeg.AsSpan().IndexOf(Encoding.ASCII.GetBytes("GPSLatitude")));
    }

    [Fact]
    public async Task GzipResult_IsDecodedWhileStreaming()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        var ply = RunnerFixture.SlimPly(50);

        var outcome = await f.Queue.AcceptResultAsync(
            runner, job.Id, new MemoryStream(RunnerFixture.Gzip(ply)), "gzip", null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal(SplatResultFormat.Ply, row.ResultFormat);
        Assert.Equal(ply.LongLength, row.ResultBytes);
    }

    [Theory]
    [InlineData("br", RunnerJobOutcome.UnsupportedEncoding)]
    [InlineData("gzip-bomb", RunnerJobOutcome.TooLarge)]
    [InlineData("gzip-corrupt", RunnerJobOutcome.Invalid)]
    public async Task GzipResult_OnlyGzip_TheCapCountsDecodedBytes_AndGarbageIsRefused(string kind, RunnerJobOutcome expected)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { MaxResultBytes = 64 * 1024, ClaimWait = TimeSpan.Zero });
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        var body = kind switch
        {
            "gzip-bomb" => RunnerFixture.Gzip(new byte[1024 * 1024]), // ~1 KB on the wire, 1 MB decoded
            "gzip-corrupt" => [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 3, 0xde, 0xad, 0xbe, 0xef, 0x00, 0x11],
            _ => RunnerFixture.Gzip(RunnerFixture.SlimPly()),
        };

        var outcome = await f.Queue.AcceptResultAsync(
            runner, job.Id, new MemoryStream(body), kind == "br" ? "br" : "gzip", null, CancellationToken.None);

        Assert.Equal(expected, outcome);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name.EndsWith(".upl", StringComparison.Ordinal));
        await using var db = h.CreateContext();
        Assert.Equal(GpuJobStatus.Claimed, (await db.GpuJobs.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("../escape.jpg")]
    [InlineData("dataset/images/../../x.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("original/IMG_2770.HEIC")]
    [InlineData("dataset/images/notes.txt")]
    public void Bundle_WithAnUnexpectedEntry_IsRefused(string name)
    {
        Assert.Throws<InvalidDataException>(() => RunnerBundle.Sanitize(RunnerFixture.Bundle((name, [1, 2, 3]))));
    }
}
