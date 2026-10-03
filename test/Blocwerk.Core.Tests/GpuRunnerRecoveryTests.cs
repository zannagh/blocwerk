// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// How a job recovers from its runner's trouble: a failed job goes to another runner first, a shutdown that kept its
/// progress costs nothing, a restarted runner gets its job back, a job that is over says so to its last holder, an upload
/// is not cut off by the job's wall-clock cap, and a runner is not offered bundles larger than it downloads.
/// </summary>
public class GpuRunnerRecoveryTests
{
    private static readonly RunnerFailure Oom = new("out of memory", Retryable: true);

    [Fact]
    public async Task ARetryableFailure_GoesToAnotherOnlineRunnerFirst_ThenBackWhenAllFailedIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (small, _) = await f.AddRunnerAsync("8 GB", walls: h.WallId);
        var (big, _) = await f.AddRunnerAsync("24 GB", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(small, null, CancellationToken.None))?.Id);
        await f.Queue.FailAsync(small, job.Id, Oom, CancellationToken.None);
        Assert.Null(await f.Queue.TryClaimAsync(small, null, CancellationToken.None));
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(big, null, CancellationToken.None))?.Id);

        // Both failed it: whoever asks next tries again; the attempt budget still ends it.
        await f.Queue.FailAsync(big, job.Id, Oom, CancellationToken.None);
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(small, null, CancellationToken.None))?.Id);
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal([small.Id, big.Id], GpuJobFailedRunners.Parse(row.FailedRunnerIdsJson));
    }

    [Fact]
    public async Task ARetryableFailure_ComesBackToTheSameRunner_WhenNoOtherIsOnline()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (only, _) = await f.AddRunnerAsync("only", walls: h.WallId);
        await f.AddRunnerAsync("asleep", online: false, walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        await f.Queue.TryClaimAsync(only, null, CancellationToken.None);
        await f.Queue.FailAsync(only, job.Id, Oom, CancellationToken.None);

        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(only, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task AnOwnRunnerThatFailedTheJob_DoesNotKeepAnApprovedSharedRunnerFromHelping()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (own, _) = await f.AddRunnerAsync("own", walls: h.WallId);
        var (shared, _) = await f.AddRunnerAsync("shared", shared: true, walls: other);
        await f.ApproveAsync(h.WallId, shared);
        var job = await f.AddJobAsync(h.WallId);

        Assert.Null(await f.Queue.TryClaimAsync(shared, null, CancellationToken.None)); // the wall's own runner is online
        await f.Queue.TryClaimAsync(own, null, CancellationToken.None);
        await f.Queue.FailAsync(own, job.Id, Oom, CancellationToken.None);

        Assert.Null(await f.Queue.TryClaimAsync(own, null, CancellationToken.None));
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(shared, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task Shutdowns_WhoseCheckpointAdvanced_AreFree_OthersUseTheShutdownBudget()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxFreeShutdowns = 1, MaxAttempts = 2 });
        var (home, _) = await f.AddRunnerAsync("home PC", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        foreach (var step in new int?[] { 5000, 10000, 15000, 15000, null })
        {
            Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(home, null, CancellationToken.None))?.Id);
            var shutdown = new RunnerFailure("the runner was shut down", true, Shutdown: true, CheckpointStep: step);
            Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.FailAsync(home, job.Id, shutdown, CancellationToken.None));
        }

        // Three evenings with progress cost nothing; the 4th (no new checkpoint) is the one free shutdown; the 5th costs.
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Queued, 2, 1, 15000), (row.Status, row.ShutdownCount, row.FailureCount, row.CheckpointStep));
        Assert.Null(row.FailedRunnerIdsJson); // shutting down is not failing the job
    }

    [Fact]
    public async Task APause_RequeuesTheJob_WithoutTouchingAnyBudget()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        await using var db = h.CreateContext();
        var held = await db.GpuJobs.AsNoTracking().SingleAsync();

        Assert.True(await f.Queue.ReleaseAsync(db, held, GpuJobQueue.ReleaseKind.Pause, "paused", CancellationToken.None));

        var row = await db.GpuJobs.AsNoTracking().SingleAsync();
        Assert.Equal((GpuJobStatus.Queued, (Guid?)null), (row.Status, row.ClaimedByRunnerId));
        Assert.Equal((0, 0, 0), (row.FailureCount, row.LostLeaseCount, row.ShutdownCount));
    }

    [Fact]
    public async Task ARestartedRunner_GetsItsSilentJobBack_WithoutLosingALease()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(0.2, null, null, "train", null), CancellationToken.None);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None)); // still reporting: maybe a sibling process

        f.Clock.Advance(GpuJobQueue.ReattachSilence);
        var claim = await f.Queue.ClaimAsync(runner, TimeSpan.Zero, new RunnerClaimRequest(null), CancellationToken.None);

        Assert.Equal((job.Id, true), (claim?.JobId, claim?.Reattached));
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Running, runner.Id, 1, 0), (row.Status, row.ClaimedByRunnerId, row.Attempts, row.LostLeaseCount));
        Assert.Equal(f.Clock.GetUtcNow() + f.Options.Lease, row.LeaseExpiresAt);
    }

    [Fact]
    public async Task AJobThatFailedForGood_IsOverForItsLastHolder_ARequeuedOneIsNotItsAnyMore()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxAttempts = 2 });
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        var report = new RunnerProgress(0.5, null, null, "train", null);

        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        await f.Queue.FailAsync(runner, job.Id, Oom, CancellationToken.None);
        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.ProgressAsync(runner, job.Id, report, CancellationToken.None));

        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        await f.Queue.FailAsync(runner, job.Id, Oom, CancellationToken.None);
        Assert.Equal(RunnerJobOutcome.Over, await f.Queue.ProgressAsync(runner, job.Id, report, CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.Over, await f.Queue.FailAsync(runner, job.Id, Oom, CancellationToken.None));
    }

    [Fact]
    public async Task AnUploadThatStartedBeforeTheCap_IsNotCutOffByIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxJobDuration = TimeSpan.FromMinutes(30) });
        var (runner, _) = await f.AddRunnerAsync("slow uplink", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        f.Clock.Advance(TimeSpan.FromMinutes(29));
        await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(1, null, null, "upload", null), CancellationToken.None);
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, await f.Queue.SweepAsync(CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(1, null, null, "upload", null), CancellationToken.None));

        // Training (not uploading) past the cap still ends the claim (back to the queue: attempts are left).
        Assert.Equal(RunnerJobOutcome.Gone, await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(1, null, null, "train", null), CancellationToken.None));
    }

    [Fact]
    public async Task ARunner_IsNotOfferedABundleLargerThanItDownloads()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        Assert.Null(await f.Queue.TryClaimRequestAsync(runner, new RunnerClaimRequest(null, job.BundleBytes - 1), CancellationToken.None));
        Assert.Equal(job.Id, (await f.Queue.TryClaimRequestAsync(runner, new RunnerClaimRequest(null, job.BundleBytes), CancellationToken.None))?.Id);
    }
}
