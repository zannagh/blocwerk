// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A recognition job from claim to end: heartbeats renew the lease, an expired lease requeues it within the lane's
/// attempts, results and failures end it and delete its input, the sweep enforces the interactive claim deadline, cancels
/// interactive jobs left over from before a restart and drops old inputs.
/// </summary>
public class RecognitionQueueLifecycleTests
{
    private static readonly RunnerProgress Heartbeat = new(0.5, 1, 2, "run", "outlining 1/2");

    [Fact]
    public async Task Progress_RenewsTheLease_AndRecordsTheStage()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);

        f.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(pod, job.Id, Heartbeat, CancellationToken.None));
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(0, await f.Queue.SweepAsync(CancellationToken.None));

        var row = await f.RowAsync(job.Id);
        Assert.Equal((GpuJobStatus.Running, 0.5, "outlining 1/2"), (row.Status, row.Progress, row.Stage));
    }

    [Fact]
    public async Task AnotherProcessOfTheSameKey_IsNotTheHolder()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        pod.ClaimToken = "process-1";
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);

        pod.ClaimToken = "process-2";
        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.ProgressAsync(pod, job.Id, Heartbeat, CancellationToken.None));
    }

    [Fact]
    public async Task AnExpiredLease_Requeues_UntilTheLanesAttemptsAreUsedUp()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);

        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await f.Queue.SweepAsync(CancellationToken.None));
        var requeued = await f.RowAsync(job.Id);
        Assert.Equal((GpuJobStatus.Queued, (Guid?)null), (requeued.Status, requeued.ClaimedByRunnerId));
        Assert.Equal(f.Clock.GetUtcNow() + f.Options.InteractiveClaimWait, requeued.LeaseExpiresAt);

        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        await f.Queue.SweepAsync(CancellationToken.None);

        var failed = await f.RowAsync(job.Id);
        Assert.Equal((GpuJobStatus.Failed, 2), (failed.Status, failed.Attempts));
        Assert.Null(failed.InputPath);
        Assert.Equal(RunnerJobOutcome.Over, await f.Queue.ProgressAsync(pod, job.Id, Heartbeat, CancellationToken.None));
    }

    [Fact]
    public async Task AnInteractiveJobNobodyClaims_IsCancelledAtTheDeadline_ABackgroundOneStaysQueued()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var interactive = await f.EnqueueAsync(RecognitionLane.Interactive);
        var background = await f.EnqueueAsync(RecognitionLane.Background);

        f.Clock.Advance(TimeSpan.FromSeconds(18));
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(interactive.Id)).Status);

        f.Clock.Advance(TimeSpan.FromSeconds(2));
        await f.Queue.SweepAsync(CancellationToken.None);
        var cancelled = await f.RowAsync(interactive.Id);
        Assert.Equal(GpuJobStatus.Cancelled, cancelled.Status);
        Assert.Equal("no recognition runner took the job within 20 s", cancelled.Error);
        Assert.False(File.Exists(f.Runners.Files.ResolvePhysicalPath(interactive.InputPath!)));
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(background.Id)).Status);
    }

    [Fact]
    public async Task Failures_ShutdownIsFree_NonRetryableEndsTheJob()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Background);

        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        await f.Queue.FailAsync(pod, job.Id, new RunnerFailure("stopping", Retryable: true, Shutdown: true), CancellationToken.None);
        var shutdown = await f.RowAsync(job.Id);
        Assert.Equal((GpuJobStatus.Queued, 0, (string?)null), (shutdown.Status, shutdown.Attempts, shutdown.FailedRunnerIdsJson));

        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        await f.Queue.FailAsync(pod, job.Id, new RunnerFailure("photo too large", Retryable: false), CancellationToken.None);
        var failed = await f.RowAsync(job.Id);
        Assert.Equal((GpuJobStatus.Failed, "photo too large"), (failed.Status, failed.Error));
    }

    [Fact]
    public async Task AResult_EndsTheJob_DeletesItsInput_AndWakesTheWaiterAtOnce()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h, new RecognitionQueueOptions { WaiterPoll = TimeSpan.FromMinutes(5) });
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);
        var waiting = f.Queue.WaitAsync(job.Id, CancellationToken.None);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Invalid, await f.Queue.ResultAsync(pod, job.Id, "[1]", CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ResultAsync(pod, job.Id, "{\"markers\":[]}", CancellationToken.None));

        var ended = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((GpuJobStatus.Succeeded, "{\"markers\":[]}", 1.0), (ended!.Status, ended.ResultJson, ended.Progress));
        Assert.Null(ended.InputPath);
        Assert.False(File.Exists(f.Runners.Files.ResolvePhysicalPath(job.InputPath!)));
        Assert.Equal(0, f.Waiters.Count);
    }

    [Fact]
    public async Task AWaiterThatGivesUp_CancelsTheJob_AndItsRunnerHearsItIsOver()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        using var page = new CancellationTokenSource();

        var waiting = f.Queue.WaitAsync(job.Id, page.Token);
        await page.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(GpuJobStatus.Cancelled, (await f.RowAsync(job.Id)).Status);
        Assert.Equal(RunnerJobOutcome.Over, await f.Queue.ProgressAsync(pod, job.Id, Heartbeat, CancellationToken.None));
    }

    [Fact]
    public async Task AWaiter_SeesTheClaimDeadlinePass_WithoutTheSweep()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var job = await f.EnqueueAsync(RecognitionLane.Interactive);

        f.Clock.Advance(TimeSpan.FromSeconds(21));
        var ended = await f.Queue.WaitAsync(job.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(GpuJobStatus.Cancelled, ended!.Status);
    }

    [Fact]
    public async Task ARestart_CancelsInteractiveJobsLeftOver_AndKeepsBackgroundOnes()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var held = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        var background = await f.EnqueueAsync(RecognitionLane.Background, callerKey: "capture-1");

        f.Restart();
        await f.Queue.SweepAsync(CancellationToken.None);

        Assert.Equal(GpuJobStatus.Cancelled, (await f.RowAsync(held.Id)).Status);
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(background.Id)).Status);
        Assert.Equal(background.Id, (await f.Queue.FindByCallerKeyAsync("capture-1", CancellationToken.None))?.Id);
        var fresh = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(fresh.Id)).Status);
    }

    [Fact]
    public async Task InputsOlderThanTheRetention_AreDropped_WithTheQueuedJobsTheyBelongTo()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var job = await f.EnqueueAsync(RecognitionLane.Background);
        var path = f.Runners.Files.ResolvePhysicalPath(job.InputPath!);

        f.Clock.Advance(TimeSpan.FromHours(23));
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.True(File.Exists(path));

        f.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await f.Queue.SweepAsync(CancellationToken.None);
        var row = await f.RowAsync(job.Id);
        Assert.Equal((GpuJobStatus.Cancelled, (string?)null), (row.Status, row.InputPath));
        Assert.False(File.Exists(path));
        await using var db = h.CreateContext();
        Assert.Empty(await RecognitionQueue.ReferencedFilesAsync(db, CancellationToken.None));
    }
}
