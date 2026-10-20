// <copyright file="WallEnhancementControlTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>Cancel, the step watchdog and the heartbeat: a hung run never blocks the wall for good.</summary>
public sealed class WallEnhancementControlTests
{
    [Fact]
    public async Task CancellingARunningStep_RecordsItFailed_OpensTheReview_AndTheStepCanBeRetried()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var hang = true;
        var step = new FakeEnhancementStep("new-holds", 700, WallEnhancementItemKind.NewHold, async (ctx, ct) =>
        {
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return WallEnhancementStepOutcome.Done("found nothing");
        });
        var service = f.Service(FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [hold], h => h.Radius = 0.05), step);
        await service.StartAsync(f.WallId);
        await WaitUntilAsync(async () => (await f.RunAsync()).CurrentStep == "new-holds");

        var view = await service.CancelAsync(f.WallId);

        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.Equal(WallEnhancementStepOutcomeKind.Failed, view.Steps.Single(s => s.Key == "new-holds").Outcome);
        Assert.Contains("cancelled", view.Steps.Single(s => s.Key == "new-holds").Summary);
        Assert.Equal(1, view.Counts.Single(c => c.Kind == WallEnhancementItemKind.Shape).Pending);

        hang = false;
        var retried = await service.RetryStepAsync(f.WallId, "new-holds");
        await f.Runner!.WhenIdleAsync(retried.RunId!.Value);
        Assert.Equal(WallEnhancementStepOutcomeKind.Done, (await service.GetAsync(f.WallId)).Steps.Single(s => s.Key == "new-holds").Outcome);
    }

    [Fact]
    public async Task AStepThatRunsPastTheWatchdog_IsStoppedAndRecordedFailed_AndTheNextStepRuns()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.StepTimeout = TimeSpan.FromMilliseconds(200);
        var hung = new FakeEnhancementStep("links", 600, WallEnhancementItemKind.Link, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return WallEnhancementStepOutcome.Done("never");
        });
        var next = FakeEnhancementStep.Suggesting("new-holds", 700, WallEnhancementItemKind.NewHold);

        var view = await f.StartAndWaitAsync(f.Service(hung, next));

        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.Contains("took longer", view.Steps.Single(s => s.Key == "links").Summary);
        Assert.Equal(1, next.Runs);
    }

    [Fact]
    public async Task TheHeartbeat_IsRenewedWhileAStepWorks()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
        var slow = new FakeEnhancementStep("links", 600, WallEnhancementItemKind.Link, async (_, ct) =>
        {
            await Task.Delay(400, ct);
            return WallEnhancementStepOutcome.Done("slow");
        });

        await f.StartAndWaitAsync(f.Service(slow));

        Assert.NotNull((await f.RunAsync()).HeartbeatAt);
    }

    [Fact]
    public async Task ARunWithAStaleHeartbeat_NoLongerBlocksAWallUpdate_AndSaysWhy()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await using var db = f.Harness.CreateContext();
        var run = new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id, HeartbeatAt = DateTimeOffset.UtcNow };
        db.WallEnhancementRuns.Add(run);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UserFacingException>(() => WallEnhancementGuard.EnsureNotRunningAsync(db, f.WallId));

        run.HeartbeatAt = DateTimeOffset.UtcNow - WallEnhancementGuard.StaleAfter - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync();
        Assert.True((await f.Service().GetAsync(f.WallId)).Stalled);

        await WallEnhancementGuard.EnsureNotRunningAsync(db, f.WallId);

        var stopped = await f.RunAsync();
        Assert.Equal(WallEnhancementStatus.Failed, stopped.Status);
        Assert.Contains("no sign of its job", stopped.Error);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200 && !await condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
