// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Net;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The result upload over HTTP, against disk exhaustion: a gzip bomb stops long before the size cap, one upload per
/// job (409) and a server-wide limit (429), a stalled body is cut off (408), and the disk floor refuses (507) or stops
/// an upload.
/// </summary>
public class RunnerUploadHardeningTests
{
    private const string Api = RunnerApiEndpoints.Prefix;

    [Fact]
    public async Task GzipBomb_IsStoppedByTheRatioGuard_NotAtTheSizeCap()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxResultSplats = 10_000_000 });
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await host.PostJsonAsync($"{Api}/claim", key, "{}");

        // A VALID splat of 4 M all-zero splats: 224 MB decoded from well under 1 MB on the wire. Without the ratio
        // guard it would be stored (the cap is 2 GB) and accepted.
        var bomb = new ByteArrayContent(GzipPlyOfZeros(4_000_000));
        bomb.Headers.ContentEncoding.Add("gzip");
        var response = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, bomb);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name.EndsWith(".upl", StringComparison.Ordinal));
        await using var db = h.CreateContext();
        Assert.Equal(GpuJobStatus.Claimed, (await db.GpuJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task SecondUpload_OfTheSameJob_Is409_AndOverTheServerLimit_Is429()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxConcurrentUploads = 1 });
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, a) = await f.AddRunnerAsync("a", walls: h.WallId);
        var (_, b) = await f.AddRunnerAsync("b", walls: h.WallId);
        var jobA = await f.AddJobAsync(h.WallId);
        var jobB = await f.AddJobAsync(h.WallId);
        await host.PostJsonAsync($"{Api}/claim", a, "{}");
        await host.PostJsonAsync($"{Api}/claim", b, "{}");
        var ply = RunnerFixture.SlimPly();
        using var gated = new GatedUploadStream(ply[..20], ply[20..]);

        // The server-side barrier: the queue asks the disk only once the upload HOLDS its slot. (gated.Started is not
        // one: the test client pumps the head into the request pipe before the server even looked at the job.)
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Disk.OnRead = _ => holding.TrySetResult();
        var first = host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{jobA.Id}/result", a, new StreamContent(gated));
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await gated.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var again = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{jobA.Id}/result", a, new ByteArrayContent(ply));
        var other = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{jobB.Id}/result", b, new ByteArrayContent(ply));
        gated.Release();

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, other.StatusCode);
        Assert.NotNull(other.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{jobB.Id}/result", b, new ByteArrayContent(ply))).StatusCode);
    }

    [Fact]
    public async Task StalledUpload_IsCutOffAtTheMaxDuration_AndFreesItsSlot()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(
            h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxUploadDuration = TimeSpan.FromSeconds(1), MaxConcurrentUploads = 1 });
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await host.PostJsonAsync($"{Api}/claim", key, "{}");
        var ply = RunnerFixture.SlimPly();
        using var stalled = new GatedUploadStream(ply[..20], ply[20..]);

        var response = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, new StreamContent(stalled))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name.EndsWith(".upl", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, new ByteArrayContent(ply))).StatusCode);
    }

    [Fact]
    public async Task LowDisk_RefusesTheUpload_AndADiskFillingUpMidUpload_StopsIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await host.PostJsonAsync($"{Api}/claim", key, "{}");
        var big = RunnerFixture.SlimPly(400_000); // 22 MB: past the first in-flight disk check

        f.Disk.Free = f.Options.MinFreeDiskBytes - 1;
        var refused = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, new ByteArrayContent(big));
        f.Disk.Free = null;
        f.Disk.OnRead = d => d.Free = d.Reads > 1 ? 0 : null; // fine at the start, full while writing
        var stopped = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, new ByteArrayContent(big));

        Assert.Equal(HttpStatusCode.InsufficientStorage, refused.StatusCode);
        Assert.Equal(HttpStatusCode.InsufficientStorage, stopped.StatusCode);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name.EndsWith(".upl", StringComparison.Ordinal));
    }

    private static byte[] GzipPlyOfZeros(int count)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(RunnerFixture.PlyHeader(count));
            var zeros = new byte[1024 * 1024];
            for (var left = (long)count * RunnerFixture.SlimColumns * 4; left > 0; left -= zeros.Length)
            {
                gzip.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
            }
        }

        return output.ToArray();
    }
}
