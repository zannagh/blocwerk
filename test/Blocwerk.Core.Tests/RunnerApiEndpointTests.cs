// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The runner API over HTTP (real routing, rate limiting and scheme selection): 401 without a valid key, 404 for
/// another runner's job, 410 once the job was taken away, 413 before the body is read, 429 with Retry-After, the
/// claim's quality filter, gzip uploads, a shutdown that costs nothing, and a runner key that is nobody anywhere else.
/// </summary>
public class RunnerApiEndpointTests
{
    private const string Api = RunnerApiEndpoints.Prefix;

    [Fact]
    public async Task NoKey_UnknownKey_OrRevokedKey_Is401()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (runner, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostJsonAsync($"{Api}/claim", null, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostJsonAsync($"{Api}/claim", GpuRunnerTokens.Create().Token, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);

        await f.Queue.ReleaseRunnerAsync(runner.Id, CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            (await db.GpuRunners.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostJsonAsync($"{Api}/hello", key, "{}")).StatusCode);
    }

    [Fact]
    public async Task AnotherRunnersJob_Is404_AndACancelledJob_Is410()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, mine) = await f.AddRunnerAsync("mine", walls: h.WallId);
        var (_, theirs) = await f.AddRunnerAsync("theirs", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/claim", mine, "{}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", theirs)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"{Api}/jobs/{job.Id}/progress", theirs, "{\"fraction\":0.5}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"{Api}/jobs/{Guid.NewGuid()}/fail", mine, "{\"retryable\":true}")).StatusCode);

        var bundle = await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", mine);
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        Assert.Equal(job.BundleBytes, (await bundle.Content.ReadAsByteArrayAsync()).LongLength);

        await f.Queue.CancelForCaptureAsync(job.CaptureId, "cancelled by an admin", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Gone, (await host.PostJsonAsync($"{Api}/jobs/{job.Id}/progress", mine, "{\"fraction\":0.6}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", mine)).StatusCode);
    }

    [Fact]
    public async Task OversizedResult_Is413_BeforeTheBodyIsRead()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxResultBytes = 1024 });
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await host.PostJsonAsync($"{Api}/claim", key, "{}");
        var content = new ByteArrayContent(new byte[4096]); // Content-Length 4096, over the cap

        var response = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name.EndsWith(".upl", StringComparison.Ordinal));
        await using var db = h.CreateContext();
        Assert.Equal(GpuJobStatus.Claimed, (await db.GpuJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Claim_HonoursTheQualityFilter_AndAGzipResultCompletesTheJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", maxQuality: null, walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId, SplatQuality.Max);

        var hello = "{\"gpuName\":\"RTX 4090\",\"vramMb\":24564,\"maxQuality\":\"ultra\",\"trainer\":\"gsplat\",\"cuda\":true}";
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/hello", key, hello)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostJsonAsync($"{Api}/claim", key, "{\"maxQuality\":\"high\"}")).StatusCode);
        var claim = await host.PostJsonAsync($"{Api}/claim", key, "{\"maxQuality\":\"ultra\"}");
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Contains("\"quality\":\"max\"", await claim.Content.ReadAsStringAsync());

        var upload = new ByteArrayContent(RunnerFixture.Gzip(RunnerFixture.SlimPly()));
        upload.Headers.ContentEncoding.Add("gzip");
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, upload)).StatusCode);

        await using var db = h.CreateContext();
        var runner = await db.GpuRunners.SingleAsync();
        Assert.Equal(("ultra", "gsplat"), (runner.MaxQuality, runner.Trainer));
        Assert.Equal(GpuJobStatus.Succeeded, (await db.GpuJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task ShutdownFailure_OverHttp_RequeuesWithoutUsingAnAttempt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await host.PostJsonAsync($"{Api}/claim", key, "{}");

        var fail = "{\"reason\":\"the runner was shut down\",\"retryable\":true,\"shutdown\":true}";
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/jobs/{job.Id}/fail", key, fail)).StatusCode);

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Queued, 0, 0), (row.Status, row.FailureCount, row.LostLeaseCount));
    }

    [Fact]
    public async Task BothRateLimitPolicies_Work_AtOnce_EachWithItsOwnRejection()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var key = GpuRunnerTokens.Create().Token;

        HttpResponseMessage? login = null;
        for (var i = 0; i < Authentication.Endpoints.ApiKeyLoginEndpoints.PermitsPerWindow + 1; i++)
        {
            login = await host.SendAsync(HttpMethod.Post, RunnerApiTestHost.LoginProbe, null);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, login!.StatusCode);
        Assert.False(login.Headers.Contains("Retry-After"));

        // The login's exhausted window does not touch the runners' bucket, and theirs answers with Retry-After.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostJsonAsync($"{Api}/hello", key, "{}")).StatusCode);
        HttpResponseMessage? runner = null;
        for (var i = 0; i < RunnerApiEndpoints.AddressBurst + 10 && runner?.StatusCode != HttpStatusCode.TooManyRequests; i++)
        {
            runner = await host.PostJsonAsync($"{Api}/hello", key, "{}");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, runner!.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(RunnerApiEndpoints.RetryAfterSeconds), runner.Headers.RetryAfter?.Delta);
    }

    [Theory]
    [InlineData("/api/v1/me")]
    [InlineData("/api/captures/probe")]
    [InlineData("/api/walls/00000000-0000-0000-0000-000000000001/probe")]
    public async Task RunnerKey_IsAnonymousEverywhereElse_EvenWherePermissiveHandlersWait(string path)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);

        var asRunner = await (await host.SendAsync(HttpMethod.Get, path, key)).Content.ReadAsStringAsync();
        var asApiKey = await (await host.SendAsync(HttpMethod.Get, path, "bwk_" + new string('a', 64))).Content.ReadAsStringAsync();

        Assert.Equal("anonymous", asRunner);
        Assert.Equal(PermissiveAuthHandler.Name, asApiKey); // the probe would notice a forwarded key
    }
}
