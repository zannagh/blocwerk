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
/// The limits around the recovery rules: re-attaches are budgeted, a second process of the same key cannot train a
/// job, giving up on an unreachable server costs no attempt, a failed job is left only to idle runners and never for
/// long, and a shutdown's checkpoint step and the free pauses are capped.
/// </summary>
public class GpuRunnerClaimSafetyTests
{
    private static readonly RunnerFailure Oom = new("out of memory", Retryable: true);
    private static readonly RunnerProgress Training = new(0.3, null, null, "train", null);

    [Fact]
    public async Task ACrashLoopingRunner_UsesItsFreeReattaches_ThenLostLeases_ThenTheJobFails()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(
            h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxReattaches = 2, MaxLostLeases = 3 });
        var (runner, _) = await f.AddRunnerAsync("crashing", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        var reattached = new List<bool>();
        for (var i = 0; i < 5; i++)
        {
            f.Clock.Advance(GpuJobQueue.ReattachSilence);
            reattached.Add(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None) is not null);
        }

        Assert.Equal([true, true, true, true, false], reattached);
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal((GpuJobStatus.Failed, 4, 3), (row.Status, row.ReattachCount, row.LostLeaseCount));
        Assert.Equal("the 3D runner kept restarting", row.Error);
    }

    [Fact]
    public async Task ASecondProcessOfTheSameKey_CannotReportOnTheJob_UntilItTookTheJobOver()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("two containers", walls: h.WallId);
        var (first, second, old) = (As(runner, "first"), As(runner, "second"), As(runner, null));
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(first, null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.ProgressAsync(second, job.Id, Training, CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.FailAsync(second, job.Id, Oom, CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(first, job.Id, Training, CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(old, job.Id, Training, CancellationToken.None)); // no token: not checked

        f.Clock.Advance(GpuJobQueue.ReattachSilence);
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(second, null, CancellationToken.None))?.Id);
        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.ProgressAsync(first, job.Id, Training, CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(second, job.Id, Training, CancellationToken.None));
    }

    [Fact]
    public async Task TheClaimTokenHeader_IsCheckedOverHttp()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        Assert.Equal(HttpStatusCode.OK, (await Send(host, key, "a", $"{RunnerApiEndpoints.Prefix}/claim", "{}")).StatusCode);
        var progress = $"{RunnerApiEndpoints.Prefix}/jobs/{job.Id}/progress";
        Assert.Equal(HttpStatusCode.NotFound, (await Send(host, key, "b", progress, "{\"fraction\":0.5}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(host, key, "a", progress, "{\"fraction\":0.5}")).StatusCode);
    }

    [Fact]
    public async Task GivingUpOnAnUnreachableServer_CostsALostLease_NotAnAttempt_AndDoesNotMarkTheRunner()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        var gaveUp = new RunnerFailure("the runner gave up: upload: 507", Retryable: true, Unreachable: true);
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.FailAsync(runner, job.Id, gaveUp, CancellationToken.None));

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Queued, 0, 1), (row.Status, row.FailureCount, row.LostLeaseCount));
        Assert.Null(row.FailedRunnerIdsJson);
    }

    [Fact]
    public async Task AFailedJob_IsNotLeftToABusyRunner()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (small, _) = await f.AddRunnerAsync("8 GB", walls: h.WallId);
        var (big, _) = await f.AddRunnerAsync("24 GB", walls: h.WallId);
        await f.AddJobAsync(h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(big, null, CancellationToken.None); // busy with the older job
        await f.Queue.TryClaimAsync(small, null, CancellationToken.None);
        await f.Queue.FailAsync(small, job.Id, Oom, CancellationToken.None);

        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(small, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task AFailedJob_ThatNoOtherRunnerTakes_GoesBackToTheRunnerThatFailedIt_AfterAWhile()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (small, _) = await f.AddRunnerAsync("8 GB", walls: h.WallId);
        var (big, _) = await f.AddRunnerAsync("idle but never claims", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(small, null, CancellationToken.None);
        await f.Queue.FailAsync(small, job.Id, Oom, CancellationToken.None);
        Assert.Null(await f.Queue.TryClaimAsync(small, null, CancellationToken.None));

        f.Clock.Advance(GpuJobQueue.LeaveToOthersFor);
        await f.MarkOnlineAsync(big, f.Clock.GetUtcNow());
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(small, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task ACheckpointStep_IsCappedAtTheTotalSteps_AndFreePausesAreCapped()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxPauses = 2 });
        var (runner, _) = await f.AddRunnerAsync("home PC", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await using (var db = h.CreateContext())
        {
            await db.GpuJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.TotalSteps, 20000));
        }

        foreach (var step in new[] { 5000, 10000, 999_999, 30000 })
        {
            await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
            await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("shut down", true, Shutdown: true, CheckpointStep: step), CancellationToken.None);
        }

        // 5000, 10000: free; 20000 (capped): over the pause cap, a shutdown; 30000: capped to 20000, not further.
        await using var check = h.CreateContext();
        var row = await check.GpuJobs.SingleAsync();
        Assert.Equal((20000, 3, 2), (row.CheckpointStep, row.PauseCount, row.ShutdownCount));
    }

    private static GpuRunner As(GpuRunner runner, string? token) => new()
    {
        Id = runner.Id, Name = runner.Name, OwnerUserId = runner.OwnerUserId, KeyHash = runner.KeyHash,
        KeyPrefix = runner.KeyPrefix, SharedWithOtherWalls = runner.SharedWithOtherWalls, ClaimToken = token,
    };

    private static Task<HttpResponseMessage> Send(RunnerApiTestHost host, string key, string token, string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add(RunnerApiEndpoints.ClaimTokenHeader, token);
        return host.Client.SendAsync(request);
    }
}
