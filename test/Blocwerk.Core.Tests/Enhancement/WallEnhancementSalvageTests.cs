// <copyright file="WallEnhancementSalvageTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>A step stopped from outside: its writes still get review items, a hung step is let go, a shutdown stays resumable.</summary>
public sealed class WallEnhancementSalvageTests
{
    [Fact]
    public async Task ACancelledStepsWrites_GetReviewItems_AndDiscardUndoesThem()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var written = new TaskCompletionSource();
        var service = f.Service(WritesThenWaits(f, hold, written, release: null));
        await service.StartAsync(f.WallId);
        await Eventually.SignalAsync(written.Task, "the step's write");

        await service.CancelAsync(f.WallId);
        await f.Runner!.WhenIdleAsync((await f.RunAsync()).Id);

        Assert.Equal(hold, Assert.Single(await f.ItemsAsync()).HoldId);
        await service.DiscardAsync(f.WallId);
        Assert.Equal(0.03, (await f.GetHoldAsync(hold)).Radius);
    }

    /// <summary>A step the watchdog times out but that does not stop: what it wrote is listed, and Discard undoes it.</summary>
    /// <remarks>
    /// The watchdog runs on a hand-fired clock and fires only once the step's write is committed. With a real 150 ms
    /// timer a loaded CI machine could time the step out while its write was still in flight; the write then honours
    /// the cancellation, nothing is written and there is nothing to review (correct, but not what this test is about).
    /// </remarks>
    [Fact]
    public async Task AStepTheWatchdogCouldNotStop_StillGetsItsReviewItems_AndDiscardUndoesThem()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        (f.Clock, f.StopGrace) = (new ManualTimeProvider(), TimeSpan.FromMilliseconds(100));
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var (written, release) = (new TaskCompletionSource(), new TaskCompletionSource());
        var service = f.Service(WritesThenWaits(f, hold, written, release));
        await service.StartAsync(f.WallId);
        await Eventually.SignalAsync(written.Task, "the step's write");

        f.Clock.FireAll();
        await Eventually.UntilAsync(async () => (await f.RunAsync()).Status == WallEnhancementStatus.Failed, "the run to stop");

        Assert.Equal(hold, Assert.Single(await f.ItemsAsync()).HoldId);
        release.SetResult();
        await f.Runner!.WhenIdleAsync((await f.RunAsync()).Id);
        await service.DiscardAsync(f.WallId);
        Assert.Equal(0.03, (await f.GetHoldAsync(hold)).Radius);
    }

    /// <summary>
    /// The CI failure's interleaving, pinned: the watchdog fires while the step's write is still on its way. The write
    /// honours the cancellation, so nothing is written, nothing is listed, and the run reviews a failed step: correct.
    /// </summary>
    [Fact]
    public async Task AStepTimedOutBeforeItsWriteLands_WritesNothing_AndHasNothingToReview()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.Clock = new ManualTimeProvider();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var entered = new TaskCompletionSource();
        var slow = new FakeEnhancementStep("outlines", 100, WallEnhancementItemKind.Shape, async (ctx, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            await using var db = await f.Journalling.CreateDbContextAsync(ct);
            (await db.Holds.FirstAsync(h => h.Id == hold, ct)).Radius = 0.05;
            await db.SaveChangesAsync(ct);
            return WallEnhancementStepOutcome.Done("never");
        });
        var service = f.Service(slow);
        var runId = (await service.StartAsync(f.WallId)).RunId!.Value;
        await Eventually.SignalAsync(entered.Task, "the step to start");

        f.Clock.FireAll();
        await Eventually.SignalAsync(f.Runner!.WhenIdleAsync(runId), "the run to finish");

        var view = await service.GetAsync(f.WallId);
        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.Contains("took longer", Assert.Single(view.Steps).Summary);
        Assert.Empty(await f.ItemsAsync());
        Assert.Equal(0.03, (await f.GetHoldAsync(hold)).Radius);
    }

    [Fact]
    public async Task AStepThatNeverReturns_IsLetGoAfterTheObserveLimit_SoTheWallIsFreeAgain()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        (f.Clock, f.StopGrace, f.ObserveLimit) = (new ManualTimeProvider(), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var written = new TaskCompletionSource();
        var service = f.Service(WritesThenWaits(f, hold, written, release: new TaskCompletionSource()));
        await service.StartAsync(f.WallId);
        var runId = (await f.RunAsync()).Id;
        await Eventually.SignalAsync(written.Task, "the step's write");

        f.Clock.FireAll();
        await Eventually.SignalAsync(f.Runner!.WhenIdleAsync(runId), "the run to be let go");

        Assert.False(WallEnhancementRunner.IsLive(runId));
        await using var db = f.Harness.CreateContext();
        await WallEnhancementGuard.EnsureNotRunningAsync(db, f.WallId);
        var discarded = await service.DiscardAsync(f.WallId);
        Assert.Equal(1, discarded.HoldsRestored);
    }

    [Fact]
    public async Task AShutdownDuringAStepThatIgnoresIt_LeavesTheRunResumable()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.StopGrace = TimeSpan.FromMilliseconds(100);
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var (written, release) = (new TaskCompletionSource(), new TaskCompletionSource());
        var job = f.Job([WritesThenWaits(f, hold, written, release)]);
        await using (var db = f.Harness.CreateContext())
        {
            db.WallEnhancementRuns.Add(new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id });
            await db.SaveChangesAsync();
        }

        var runId = (await f.RunAsync()).Id;
        using var shutdown = new CancellationTokenSource();
        var running = job.RunAsync(runId, shutdown.Token);
        await Eventually.SignalAsync(written.Task, "the step's write");

        await shutdown.CancelAsync();
        await Eventually.SignalAsync(running, "the job to return on shutdown");

        var run = await f.RunAsync();
        Assert.Equal(WallEnhancementStatus.Running, run.Status);
        Assert.Null(run.Error);
        release.SetResult();
    }

    [Fact]
    public async Task AWallUpdateStoppingAStaleRun_ListsTheRunningStepsWritesFirst()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        WallEnhancementStepRecord[] records =
        [
            new("outlines", "Outlines", WallEnhancementStepOutcomeKind.Running, string.Empty, started, null, [], Kind: WallEnhancementItemKind.Shape,
                Labels: [WallEnhancementKinds.BatchLabel("outlines")]),
        ];
        await using (var db = f.Harness.CreateContext())
        {
            db.WallEnhancementRuns.Add(new WallEnhancementRun
            {
                WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id, StepsJson = WallEnhancementStepRecord.Write(records),
                CurrentStep = "outlines", HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            });
            await db.SaveChangesAsync();
        }

        using (f.Journal.BeginBatch(WallEnhancementKinds.BatchLabel("outlines"), Enums.ChangeJournalScopeKind.Wall, f.WallId))
        {
            await using var db = await f.Journalling.CreateDbContextAsync();
            db.CurrentUserId = f.Harness.Owner.Id;
            (await db.Holds.FirstAsync(h => h.Id == hold)).Radius = 0.05;
            await db.SaveChangesAsync();
        }

        await using (var db = f.Harness.CreateContext())
        {
            await WallEnhancementGuard.EnsureNotRunningAsync(db, f.WallId);
        }

        Assert.Equal(WallEnhancementStatus.Failed, (await f.RunAsync()).Status);
        Assert.Equal(hold, Assert.Single(await f.ItemsAsync()).HoldId);
        Assert.Single(WallEnhancementStepRecord.Parse((await f.RunAsync()).StepsJson).Single().BatchIds);
    }

    /// <summary>
    /// A step that writes one hold in its batch, signals <paramref name="written"/> once that write is committed, then
    /// waits: for its cancellation (no <paramref name="release"/>), or deaf to it for the release only.
    /// </summary>
    private static FakeEnhancementStep WritesThenWaits(
        WallEnhancementFixture f, Guid hold, TaskCompletionSource written, TaskCompletionSource? release) =>
        new("outlines", 100, WallEnhancementItemKind.Shape, async (ctx, ct) =>
        {
            await using (var db = await f.Journalling.CreateDbContextAsync(ct))
            {
                db.CurrentUserId = ctx.UserId;
                (await db.Holds.FirstAsync(h => h.Id == hold, ct)).Radius = 0.05;
                await db.SaveChangesAsync(ct);
            }

            written.TrySetResult();
            if (release is null)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                await release.Task;
            }

            return WallEnhancementStepOutcome.Done("written");
        });
}
