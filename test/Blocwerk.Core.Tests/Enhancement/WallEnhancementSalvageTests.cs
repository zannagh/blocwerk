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
        var step = WritesThenWaits(f, hold, deaf: false, release: null);
        var service = f.Service(step);
        await service.StartAsync(f.WallId);
        await WaitUntilAsync(async () => (await f.GetHoldAsync(hold)).Radius > 0.04);

        await service.CancelAsync(f.WallId);
        await f.Runner!.WhenIdleAsync((await f.RunAsync()).Id);

        Assert.Equal(hold, Assert.Single(await f.ItemsAsync()).HoldId);
        await service.DiscardAsync(f.WallId);
        Assert.Equal(0.03, (await f.GetHoldAsync(hold)).Radius);
    }

    [Fact]
    public async Task AStepTheWatchdogCouldNotStop_StillGetsItsReviewItems_AndDiscardUndoesThem()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        (f.StepTimeout, f.StopGrace) = (TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(100));
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var release = new TaskCompletionSource();
        var service = f.Service(WritesThenWaits(f, hold, deaf: true, release));
        await service.StartAsync(f.WallId);
        await WaitUntilAsync(async () => (await f.RunAsync()).Status == WallEnhancementStatus.Failed);

        Assert.Equal(hold, Assert.Single(await f.ItemsAsync()).HoldId);
        release.SetResult();
        await f.Runner!.WhenIdleAsync((await f.RunAsync()).Id);
        await service.DiscardAsync(f.WallId);
        Assert.Equal(0.03, (await f.GetHoldAsync(hold)).Radius);
    }

    [Fact]
    public async Task AStepThatNeverReturns_IsLetGoAfterTheObserveLimit_SoTheWallIsFreeAgain()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        (f.StepTimeout, f.StopGrace, f.ObserveLimit) = (TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var service = f.Service(WritesThenWaits(f, hold, deaf: true, release: new TaskCompletionSource()));
        await service.StartAsync(f.WallId);
        var runId = (await f.RunAsync()).Id;

        await f.Runner!.WhenIdleAsync(runId).WaitAsync(TimeSpan.FromSeconds(10));

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
        var release = new TaskCompletionSource();
        var job = f.Job([WritesThenWaits(f, hold, deaf: true, release)]);
        await using (var db = f.Harness.CreateContext())
        {
            db.WallEnhancementRuns.Add(new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id });
            await db.SaveChangesAsync();
        }

        var runId = (await f.RunAsync()).Id;
        using var shutdown = new CancellationTokenSource();
        var running = job.RunAsync(runId, shutdown.Token);
        await WaitUntilAsync(async () => (await f.GetHoldAsync(hold)).Radius > 0.04);

        await shutdown.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

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

    /// <summary>A step that writes one hold in its batch, then waits: cooperatively, or (deaf) for a release only.</summary>
    private static FakeEnhancementStep WritesThenWaits(WallEnhancementFixture f, Guid hold, bool deaf, TaskCompletionSource? release) =>
        new("outlines", 100, WallEnhancementItemKind.Shape, async (ctx, ct) =>
        {
            await using (var db = await f.Journalling.CreateDbContextAsync(ct))
            {
                db.CurrentUserId = ctx.UserId;
                (await db.Holds.FirstAsync(h => h.Id == hold, ct)).Radius = 0.05;
                await db.SaveChangesAsync(ct);
            }

            if (deaf)
            {
                await release!.Task;
            }
            else
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return WallEnhancementStepOutcome.Done("written");
        });

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 250 && !await condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
