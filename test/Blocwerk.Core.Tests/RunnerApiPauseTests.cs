// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A runner its owner paused: the hello's <c>paused</c> is stored and listed (older runners send none), a paused runner
/// gets no work routed to it, a claim clears the flag, and a mid-job pause hands the job back at no cost without marking
/// the runner offline.
/// </summary>
public class RunnerApiPauseTests
{
    private const string Api = RunnerApiEndpoints.Prefix;

    [Fact]
    public async Task HelloPaused_IsStored_AndListed_AndAnOldHelloSendsNone()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (runner, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var service = new GpuRunnerService(h.DbContextFactory, h.CurrentUser, f.Queue, NullLogger<GpuRunnerService>.Instance, null);

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/hello", key, "{\"gpuName\":\"RTX\",\"paused\":true}")).StatusCode);
        Assert.True(await PausedAsync(h, runner.Id));
        Assert.True((await service.ListForWallAsync(h.WallId)).Single().IsPaused);

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/hello", key, "{\"gpuName\":\"RTX\",\"paused\":false}")).StatusCode);
        Assert.False(await PausedAsync(h, runner.Id));
        Assert.False((await service.ListForWallAsync(h.WallId)).Single().IsPaused);

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/hello", key, "{\"gpuName\":\"RTX\"}")).StatusCode);
        Assert.Null(await PausedAsync(h, runner.Id));
        Assert.False((await service.ListForWallAsync(h.WallId)).Single().IsPaused);
    }

    [Fact]
    public async Task APausedRunner_IsNoEligibleRunner_AndAClaimClearsThePause()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (runner, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        Assert.True(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Draft, CancellationToken.None));

        await host.PostJsonAsync($"{Api}/hello", key, "{\"maxQuality\":\"max\",\"paused\":true}");
        Assert.False(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Draft, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NoContent, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);
        Assert.False(await PausedAsync(h, runner.Id));
        Assert.True(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Draft, CancellationToken.None));
    }

    [Fact]
    public async Task PauseMidJob_RequeuesForFree_AndKeepsTheRunnerOnline()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (runner, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);

        // No checkpoint step: a plain shutdown would cost a free shutdown; a pause costs nothing.
        var fail = "{\"reason\":\"paused\",\"retryable\":true,\"shutdown\":true,\"pause\":true}";
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/jobs/{job.Id}/fail", key, fail)).StatusCode);

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Queued, 1, 0, 0), (row.Status, row.PauseCount, row.ShutdownCount, row.FailureCount));
        var seen = (await db.GpuRunners.SingleAsync(r => r.Id == runner.Id)).LastSeenAt;
        Assert.True(seen >= f.Clock.GetUtcNow() - f.Options.OnlineWindow);
    }

    [Fact]
    public async Task TheClaim_NamesTheWallAndTheCapture()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        var body = await (await host.PostJsonAsync($"{Api}/claim", key, "{}")).Content.ReadAsStringAsync();

        Assert.Contains($"\"wallId\":\"{h.WallId}\"", body);
        Assert.Contains($"\"captureId\":\"{job.CaptureId}\"", body);
    }

    private static async Task<bool?> PausedAsync(WallTestHarness h, Guid runnerId)
    {
        await using var db = h.CreateContext();
        return (await db.GpuRunners.SingleAsync(r => r.Id == runnerId)).Paused;
    }
}
