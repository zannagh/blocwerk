// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The slow after-completion steps (hold proposals, coverage) no longer hold the capture worker: the capture completes,
/// the steps are marked and queued for <see cref="AfterCompletionWorker"/>, which runs them, clears the mark, and after a
/// restart picks a marked capture up again. A shutdown in a stage gives the attempt back before the stage unwinds.
/// </summary>
public class CaptureAfterCompletionTests
{
    [Fact]
    public async Task ACompletedCapture_QueuesItsSlowSteps_InsteadOfRunningThemOnTheCaptureWorker()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        var queue = new AfterCompletionQueue();
        CaptureFollowUpChain? chain = null;
        using var s = new CaptureScenario(
            h,
            followUps: harness => chain = FollowUpChains.Build(
                harness.RootContextFactory,
                new ScriptedFollowUpStep("place", 100, log),
                new ScriptedFollowUpStep("proposals", 400, log, afterCompletion: true)),
            afterCompletion: queue);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal(["place"], log);
        Assert.Equal(WallCaptureStatus.Succeeded, (await CaptureAsync(h, captureId)).Status);
        Assert.NotNull(Record(await CaptureAsync(h, captureId)).AfterCompletion);
        Assert.Equal(captureId, await queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        var worker = new AfterCompletionWorker(queue, chain!, h.RootContextFactory, NullLogger<AfterCompletionWorker>.Instance);
        await worker.RunAsync(captureId, CancellationToken.None);

        Assert.Equal(["place", "proposals"], log);
        var record = Record(await CaptureAsync(h, captureId));
        Assert.Null(record.AfterCompletion);
        Assert.NotNull(record.Find("proposals"));
    }

    [Fact]
    public async Task AMarkLeftByARestart_IsQueuedAgainOnStart_AndRunsOnce()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        var queue = new AfterCompletionQueue();
        CaptureFollowUpChain? chain = null;
        using var s = new CaptureScenario(
            h,
            followUps: harness => chain = FollowUpChains.Build(
                harness.RootContextFactory, new ScriptedFollowUpStep("proposals", 400, log, afterCompletion: true)),
            afterCompletion: queue);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await queue.DequeueAsync(CancellationToken.None); // the process "died" before the worker took it

        var restarted = new AfterCompletionQueue();
        var worker = new AfterCompletionWorker(restarted, chain!, h.RootContextFactory, NullLogger<AfterCompletionWorker>.Instance);
        await worker.RecoverAsync(CancellationToken.None);
        var again = await restarted.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await worker.RunAsync(again, CancellationToken.None);
        await worker.RunAsync(again, CancellationToken.None); // a second hand-off finds no mark: nothing runs twice

        Assert.Equal(captureId, again);
        Assert.Equal(["proposals"], log);
        Assert.Null(Record(await CaptureAsync(h, captureId)).AfterCompletion);
    }

    [Fact]
    public async Task AShutdownDuringAStage_GivesTheAttemptBack_BeforeTheStageHasUnwound()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        var jobId = s.Client.Adopt("solve");
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
            (capture.Status, capture.SolveJobId, capture.Attempts) = (WallCaptureStatus.Solving, jobId, 0);
            await db.SaveChangesAsync();
        }

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        s.Client.StatusGate = async () =>
        {
            entered.TrySetResult();
            await release.Task; // a call that does not stop when the shutdown starts
        };
        using var shutdown = new CancellationTokenSource();
        var run = s.Processor.ProcessAsync(captureId, shutdown.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, (await CaptureAsync(h, captureId)).Attempts);

        await shutdown.CancelAsync();
        var refunded = await WaitForAsync(async () => (await CaptureAsync(h, captureId)).Attempts == 0);
        Assert.False(run.IsCompleted);
        Assert.True(refunded, "the attempt was not given back while the stage was still busy");

        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, (await CaptureAsync(h, captureId)).Attempts); // given back once, not twice
    }

    [Fact]
    public async Task ACaptureCompletedAgainDuringTheRun_KeepsItsNewerMark_AndTheCountIsTakenBackOnly()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        var queue = new AfterCompletionQueue();
        CaptureFollowUpChain? chain = null;
        var step = new ScriptedFollowUpStep("proposals", 400, log, afterCompletion: true);
        using var s = new CaptureScenario(
            h, followUps: harness => chain = FollowUpChains.Build(harness.RootContextFactory, step), afterCompletion: queue);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        var first = Record(await CaptureAsync(h, captureId)).AfterCompletion;
        step.Run = (c, _) =>
        {
            chain!.MarkAfterCompletionAsync(c.CaptureId, CancellationToken.None).GetAwaiter().GetResult(); // completed again meanwhile
            return CaptureFollowUpStepResult.Done(string.Empty);
        };

        var newer = await chain!.RunAfterCompletionAsync(captureId, CancellationToken.None);

        var record = Record(await CaptureAsync(h, captureId));
        Assert.True(newer);
        Assert.NotNull(record.AfterCompletion);
        Assert.NotEqual(first, record.AfterCompletion);
        Assert.Equal(0, record.Recoveries);
    }

    [Fact]
    public async Task ACaptureWaitingLong_DoesNotHoldTheOthersBack_AndRunsOnceAtATime()
    {
        using var h = new WallTestHarness();
        var queue = new AfterCompletionQueue();
        var (slow, fast) = (Guid.NewGuid(), Guid.NewGuid());
        var release = new TaskCompletionSource();
        var fastDone = new TaskCompletionSource();
        var runs = new List<Guid>();
        var chain = FollowUpChains.Build(h.RootContextFactory);
        using var worker = new AfterCompletionWorker(queue, chain, h.RootContextFactory, NullLogger<AfterCompletionWorker>.Instance)
        {
            RunOverride = async (id, _) =>
            {
                lock (runs)
                {
                    runs.Add(id);
                }

                if (id == slow)
                {
                    await release.Task; // a runner's hold search that waits for hours
                }
                else
                {
                    fastDone.TrySetResult();
                }

                return false;
            },
        };
        await worker.StartAsync(CancellationToken.None);
        queue.Enqueue(slow);
        queue.Enqueue(slow); // handed off again while it runs: not run twice at once
        queue.Enqueue(fast);

        await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await worker.StopAsync(CancellationToken.None);

        lock (runs)
        {
            Assert.Equal(1, runs.Count(r => r == slow));
            Assert.Contains(fast, runs);
        }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<WallCapture> CaptureAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == captureId);
    }

    private static CaptureFollowUpRecord Record(WallCapture capture) => CaptureFollowUpRecord.Parse(capture.FollowUpJson);
}
