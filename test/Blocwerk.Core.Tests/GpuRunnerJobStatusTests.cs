// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The status a waiting or re-claimed job shows: online/busy from the runners' current heartbeat and claims (a runner
/// that reported its shutdown is offline at once), and no stale error once a runner trains it again.
/// </summary>
public class GpuRunnerJobStatusTests
{
    [Fact]
    public async Task AShutDownRunner_IsOfflineAtOnce_AndOnlineAgainWithItsNextCall()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, token) = await f.AddRunnerAsync("cellar", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.FailAsync(runner, job.Id, Shutdown(), CancellationToken.None));
        await f.Queue.SweepAsync(CancellationToken.None);

        Assert.EndsWith("(none online)", await StageAsync(h, job.Id));
        Assert.False(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, job.Quality, CancellationToken.None));

        Assert.NotNull(await f.Queue.AuthenticateAsync(token, CancellationToken.None));
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.Equal("waiting for a 3D runner (1 online)", await StageAsync(h, job.Id));
    }

    [Fact]
    public async Task Busy_CountsOnlyRunnersThatHoldAClaim()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("only", walls: h.WallId);
        await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        var second = await f.AddJobAsync(h.WallId);

        await f.Queue.SweepAsync(CancellationToken.None);

        Assert.Equal("waiting for a 3D runner (1 online, busy)", await StageAsync(h, second.Id));
    }

    [Theory]
    [InlineData(0, 0, "waiting for a 3D runner that can train high (none online)")]
    [InlineData(1, 0, "waiting for a 3D runner (1 online)")]
    [InlineData(2, 1, "waiting for a 3D runner (2 online, 1 busy)")]
    [InlineData(2, 2, "waiting for a 3D runner (2 online, busy)")]
    public void WaitingStage_SaysHowManyAreOnlineAndBusy(int online, int busy, string expected) =>
        Assert.Equal(expected, GpuJobQueue.WaitingStage(SplatQuality.High, online, busy));

    [Fact]
    public async Task AReclaim_ClearsTheShutdownError()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, token) = await f.AddRunnerAsync("cellar", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        await f.Queue.FailAsync(runner, job.Id, Shutdown(), CancellationToken.None);
        Assert.Equal("the 3D runner shut down", await ErrorAsync(h, job.Id));

        await f.Queue.AuthenticateAsync(token, CancellationToken.None);
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
        Assert.Null(await ErrorAsync(h, job.Id));
    }

    [Fact]
    public async Task Progress_ClearsAStaleError()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("cellar", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            await db.GpuJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Error, "the 3D runner shut down"));
        }

        var report = new RunnerProgress(0.25, 100, 400, "train", null);
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(runner, job.Id, report, CancellationToken.None));

        Assert.Null(await ErrorAsync(h, job.Id));
    }

    private static RunnerFailure Shutdown() => new("stopping", Retryable: true, Shutdown: true);

    private static async Task<string?> StageAsync(WallTestHarness h, Guid jobId)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.Where(j => j.Id == jobId).Select(j => j.Stage).SingleAsync();
    }

    private static async Task<string?> ErrorAsync(WallTestHarness h, Guid jobId)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.Where(j => j.Id == jobId).Select(j => j.Error).SingleAsync();
    }
}
