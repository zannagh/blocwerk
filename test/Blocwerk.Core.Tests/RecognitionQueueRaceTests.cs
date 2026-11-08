// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Races around a held recognition job: a heartbeat between reading the job and ending it does not lose the end, a stale
/// read from an earlier claim never renews the lease, and waiters leave background jobs and each other alone.
/// </summary>
public class RecognitionQueueRaceTests
{
    private static readonly RunnerProgress Heartbeat = new(0.5, null, null, "run", null);

    [Fact]
    public async Task AHeartbeatBetweenReadAndFinish_DoesNotLoseTheResult()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        var readWhileClaimed = await f.RowAsync(job.Id);

        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(pod, job.Id, Heartbeat, CancellationToken.None));
        await using var db = h.CreateContext();
        Assert.True(await f.Queue.FinishAsync(db, readWhileClaimed, GpuJobStatus.Succeeded, null, "{}", CancellationToken.None));

        Assert.Equal(GpuJobStatus.Succeeded, (await f.RowAsync(job.Id)).Status);
    }

    [Fact]
    public async Task ACallThatChangesNothing_IsNotReportedAccepted()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        await f.Queue.CancelAsync(job.Id, "gone", CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Over, await f.Queue.ResultAsync(pod, job.Id, "{}", CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.Over, await f.Queue.FailAsync(pod, job.Id, new RunnerFailure("x", true), CancellationToken.None));
        Assert.Equal(GpuJobStatus.Cancelled, (await f.RowAsync(job.Id)).Status);
    }

    [Fact]
    public async Task AHeartbeatFromAnEarlierClaimsRead_DoesNotRenewTheNewClaimsLease()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        pod.ClaimToken = "process-1";
        var job = await f.EnqueueAsync(RecognitionLane.Background);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        var firstClaim = await f.RowAsync(job.Id);
        await f.Queue.FailAsync(pod, job.Id, new RunnerFailure("stopping", true, Shutdown: true), CancellationToken.None);

        pod.ClaimToken = "process-2";
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        var lease = (await f.RowAsync(job.Id)).LeaseExpiresAt;
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        await using var db = h.CreateContext();

        Assert.False(await f.Queue.HeartbeatAsync(db, firstClaim, Heartbeat, CancellationToken.None));
        var row = await f.RowAsync(job.Id);
        Assert.Equal((lease, GpuJobStatus.Claimed, "process-2"), (row.LeaseExpiresAt, row.Status, row.ClaimToken));
    }

    [Fact]
    public async Task ABackgroundWaiterThatGivesUp_LeavesTheJobQueued()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var job = await f.EnqueueAsync(RecognitionLane.Background);
        using var stopping = new CancellationTokenSource();

        var waiting = f.Queue.WaitAsync(job.Id, stopping.Token);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(job.Id)).Status);
        Assert.Equal(0, f.Waiters.Count);
    }

    [Fact]
    public async Task OneWaiterLeaving_DoesNotDropTheSignalOfAnotherOnTheSameJob()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h, new RecognitionQueueOptions { WaiterPoll = TimeSpan.FromMinutes(5) });
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Background);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        using var leaving = new CancellationTokenSource();

        var staying = f.Queue.WaitAsync(job.Id, CancellationToken.None);
        var left = f.Queue.WaitAsync(job.Id, leaving.Token);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => left);
        Assert.Equal(1, f.Waiters.Count);

        await f.Queue.ResultAsync(pod, job.Id, "{}", CancellationToken.None);
        Assert.Equal(GpuJobStatus.Succeeded, (await staying.WaitAsync(TimeSpan.FromSeconds(10)))!.Status);
        Assert.Equal(0, f.Waiters.Count);
    }
}
