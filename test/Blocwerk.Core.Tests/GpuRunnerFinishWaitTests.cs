// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A runner delivered the trained view but the splat worker (which finishes it) is down: the capture shows one stable
/// "waiting for the 3D worker" state while the sweep retries, the follow-ups do not re-run per retry, and the give-up
/// says why (a re-finish gets its job and its capture back).
/// </summary>
public class GpuRunnerFinishWaitTests
{
    [Fact]
    public async Task WhileTheWorkerIsDown_TheCaptureStaysWaiting_AndFinishesWhenItIsBack()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        f.Scenario.SplatClient.SubmitError = new ComputeJobException(ComputeFailureKind.Unavailable, "unreachable");
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());

        await f.ProcessAsync();
        await AssertWaitingAsync(h);

        for (var i = 0; i < 3; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(16));
            await f.Runners.SweepAsync(CancellationToken.None);
            await AssertWaitingAsync(h); // the hand-back must not flip the capture to "finishing" and back
            await f.ProcessAsync();
            await AssertWaitingAsync(h);
        }

        Assert.Null(await FollowUpNoteAsync(h));

        f.Scenario.SplatClient.SubmitError = null;
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        await f.Runners.SweepAsync(CancellationToken.None);
        await f.ProcessAsync();

        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
        Assert.NotNull((await db.GpuJobs.SingleAsync()).InstalledAt);
        Assert.Single(await db.WallGeometrySplats.ToListAsync());
    }

    [Fact]
    public async Task GivingUp_TellsTheCaptureWhy_AndFailsTheJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        f.Scenario.SplatClient.SubmitError = new ComputeJobException(ComputeFailureKind.Unavailable, "unreachable");
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.ProcessAsync();

        f.Clock.Advance(TimeSpan.FromHours(25));
        await f.Runners.SweepAsync(CancellationToken.None);
        await f.ProcessAsync();

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, capture.Status);
        Assert.Contains("could not be finished", capture.Error, StringComparison.Ordinal);
        Assert.Equal(GpuJobStatus.Failed, (await db.GpuJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task GivingUpOnARefinish_RestoresTheJobAndTheCapture()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.ProcessAsync();
        Assert.Empty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
        f.Scenario.SplatClient.SubmitError = new ComputeJobException(ComputeFailureKind.Unavailable, "unreachable");
        await f.ProcessAsync();
        await AssertWaitingAsync(h);

        f.Clock.Advance(TimeSpan.FromHours(25));
        await f.Runners.SweepAsync(CancellationToken.None);
        await f.ProcessAsync();

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.NotEqual(WallCaptureProcessor.WaitingForWorkerStage, capture.Stage);
        var job = await db.GpuJobs.SingleAsync();
        Assert.Equal(GpuJobStatus.Succeeded, job.Status);
        Assert.NotNull(job.InstalledAt);
        Assert.Contains("view installed before stays", await FollowUpNoteAsync(h), StringComparison.Ordinal);
    }

    private static async Task AssertWaitingAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.AsNoTracking().SingleAsync();
        Assert.Equal(WallCaptureStatus.Splatting, capture.Status);
        Assert.Equal(WallCaptureProcessor.WaitingForWorkerStage, capture.Stage);
        Assert.Equal(0, capture.Attempts);
    }

    private static async Task<string?> FollowUpNoteAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var json = await db.WallCaptures.AsNoTracking().Select(c => c.FollowUpJson).SingleAsync();
        return CaptureFollowUpRecord.Parse(json).Note;
    }
}
