// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.RecognitionApiSupport;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The recognition queue over the runner API (runner protocol 1.2): the instance runner (deployment key, every wall,
/// recognition kinds only), an owned runner that takes recognition work before 3D work, the job-scoped calls finding the
/// job in either queue, and the result upload's checks.
/// </summary>
public class RecognitionRunnerApiTests
{
    [Fact]
    public async Task InstanceRunner_ServesAnyWall_RecognitionOnly_ThroughTheWholeLifecycle()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        await RegisterInstanceAsync(r, r.Options);
        var foreignWall = await r.Runners.AddWallAsync("someone else's", foreign: true);
        await r.Runners.AddJobAsync(h.WallId);
        var job = await r.EnqueueAsync(RecognitionLane.Interactive, wallId: foreignWall);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

        var hello = await host.PostJsonAsync($"{Api}/hello", InstanceKey, """{"capabilities":["markers","splat","detect-holds@2"],"memoryBudgetMb":4096}""");
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        var accepted = JsonDocument.Parse(await hello.Content.ReadAsStringAsync()).RootElement.GetProperty("capabilities");
        Assert.Equal(["markers", "detect-holds@2"], accepted.EnumerateArray().Select(c => c.GetString()));

        var (status, claim) = await ClaimAsync(host, InstanceKey);
        Assert.Equal(200, status);
        Assert.Equal(job.Id, claim!.Value.GetProperty("jobId").GetGuid());
        Assert.Equal("markers", claim.Value.GetProperty("kind").GetString());
        Assert.Equal(30, claim.Value.GetProperty("leaseSeconds").GetInt32());

        var bundle = await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", InstanceKey);
        var bytes = await bundle.Content.ReadAsByteArrayAsync();
        Assert.Equal(claim.Value.GetProperty("bundleSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var ranged = Request(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", InstanceKey, null);
        ranged.Headers.Range = new RangeHeaderValue(10, null);
        var tail = await host.Client.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, tail.StatusCode);
        Assert.Equal(bytes[10..], await tail.Content.ReadAsByteArrayAsync());

        var progress = await host.PostJsonAsync($"{Api}/jobs/{job.Id}/progress", InstanceKey, """{"fraction":0.5,"stage":"markers"}""");
        Assert.Equal(30, JsonDocument.Parse(await progress.Content.ReadAsStringAsync()).RootElement.GetProperty("leaseSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", InstanceKey, Succeeded())).StatusCode);
        var row = await r.RowAsync(job.Id);
        Assert.Equal(GpuJobStatus.Succeeded, row.Status);
        Assert.Contains("\"markers\"", row.ResultJson);

        Assert.Equal(HttpStatusCode.Gone, (await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", InstanceKey, Succeeded())).StatusCode);
        Assert.Equal(204, (await ClaimAsync(host, InstanceKey)).Status);
    }

    [Fact]
    public async Task InstanceKey_IsOnlyTheConfiguredOne_AndStopsWorkingWhenTheDeploymentDropsIt()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        var instance = await RegisterInstanceAsync(r, r.Options);
        Assert.Null(instance!.OwnerUserId);
        Assert.NotEqual(InstanceKey, instance.KeyHash);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/hello", InstanceKey, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostJsonAsync($"{Api}/hello", InstanceKey + "x", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostJsonAsync($"{Api}/hello", "short-key", "{}")).StatusCode);

        Assert.Equal(instance.Id, (await RegisterInstanceAsync(r, Options(instanceKey: false)))!.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostJsonAsync($"{Api}/hello", InstanceKey, "{}")).StatusCode);
        await RegisterInstanceAsync(r, r.Options);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/hello", InstanceKey, "{}")).StatusCode);
    }

    [Fact]
    public async Task InstanceRunner_IsNeverShownInTheRunnersUi()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        await RegisterInstanceAsync(r, r.Options);
        await r.Runners.AddRunnerAsync("mine", walls: h.WallId);
        await using (var db = h.CreateContext())
        {
            await db.Users.Where(u => u.Id == h.Owner.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Role, IdentityRole.Admin));
        }

        var progress = new JobProgressReader(h.RootContextFactory, clock: r.Clock, runnerQueue: r.Runners.Queue);
        var overview = await new GpuRunnerOverviewService(h.DbContextFactory, h.CurrentUser, r.Runners.Queue, progress, null, r.Clock).GetAsync(default);
        var all = await new GpuRunnerService(h.DbContextFactory, h.CurrentUser, r.Runners.Queue, NullLogger<GpuRunnerService>.Instance).ListAllAsync();

        Assert.Equal(["mine"], overview.Runners.Select(x => x.Name));
        Assert.Equal(["mine"], all.Select(x => x.Name));
    }

    [Fact]
    public async Task OwnedRunner_TakesRecognitionBefore3D_OnlyOnItsWalls()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        var foreignWall = await r.Runners.AddWallAsync("someone else's", foreign: true);
        var key = await OwnedRunnerAsync(r, "splat,markers");
        await r.EnqueueAsync(RecognitionLane.Interactive, wallId: foreignWall);
        var gpuJob = await r.Runners.AddJobAsync(h.WallId);
        var job = await r.EnqueueAsync(RecognitionLane.Background);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

        var (_, first) = await ClaimAsync(host, key);
        Assert.Equal(job.Id, first!.Value.GetProperty("jobId").GetGuid());
        Assert.Equal(120, first.Value.GetProperty("leaseSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/jobs/{job.Id}/fail", key, """{"reason":"oom","retryable":true}""")).StatusCode);
        Assert.Equal(GpuJobStatus.Queued, (await r.RowAsync(job.Id)).Status);

        // It failed the job, but no other runner could take it: it gets it again, and then the 3D job.
        var (_, again) = await ClaimAsync(host, key);
        Assert.Equal(job.Id, again!.Value.GetProperty("jobId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, Succeeded())).StatusCode);
        var (_, gpu) = await ClaimAsync(host, key);
        Assert.Equal(gpuJob.Id, gpu!.Value.GetProperty("jobId").GetGuid());
        Assert.Equal("splat", gpu.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Result_IsChecked_AndAnotherRunnersJobIs404()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        var key = await OwnedRunnerAsync(r, "markers");
        var other = await OwnedRunnerAsync(r, "markers");
        var job = await r.EnqueueAsync(RecognitionLane.Interactive);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);
        await ClaimAsync(host, key);
        var path = $"{Api}/jobs/{job.Id}/result";

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Put, path, other, Succeeded())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await host.SendAsync(HttpMethod.Put, path, key, ResultZip("""{"status":"failed"}"""))).StatusCode);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await host.SendAsync(HttpMethod.Put, path, key, ResultZip("""{"status":"succeeded","result":{}}""", "extra.json"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await host.SendAsync(HttpMethod.Put, path, key, new ByteArrayContent([1, 2, 3]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.SendAsync(HttpMethod.Put, path, key, new ByteArrayContent(new byte[(16 << 20) + 1]))).StatusCode);

        var gzipped = new ByteArrayContent(RunnerFixture.Gzip(await Succeeded().ReadAsByteArrayAsync()));
        gzipped.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, path, key, gzipped)).StatusCode);
        Assert.Equal(GpuJobStatus.Succeeded, (await r.RowAsync(job.Id)).Status);
    }
}
