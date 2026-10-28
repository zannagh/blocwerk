// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Cancel it" on a capture parked for a 3D runner (<c>SPLATSERVICE__ONRUNNERS</c>): nothing would wake it again, so the cancel
/// ends it. A capture waiting for its finish becomes "Model ready" and a restart trains nothing; a re-finish gets its view back;
/// a markerless capture waiting for its reconstruction fails and keeps its photos.
/// </summary>
public class RunnerSplatCancelTests
{
    private const string Reason = "cancelled by an admin";

    [Fact]
    public async Task ACaptureParkedForItsFinish_EndsAsModelReady_AndARestartTrainsNothing()
    {
        using var h = new WallTestHarness();
        using var s = RunnerSplatFlowTests.Scenario(h, out _);
        var (captureId, _) = await RunnerSplatFlowTests.ParkedForFinishAsync(h, s);

        Assert.True(await s.Runners!.CancelForCaptureAsync(captureId, Reason, CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await s.Runners.SweepAsync(CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal((WallCaptureStatus.Succeeded, "Done"), (capture.Status, capture.Stage));
        Assert.Null(capture.SplatJobId);
        Assert.Equal(GpuJobStatus.Cancelled, (await db.GpuJobs.SingleAsync(j => j.Kind == GpuJobKind.SplatFinish)).Status);
        Assert.Equal(1, await db.GpuJobs.CountAsync(j => j.Kind == GpuJobKind.Splat));
        Assert.False(await db.GpuJobs.AnyAsync(j => j.Status == GpuJobStatus.Queued));
        Assert.Null((await s.Service.GetCaptureAsync(captureId))!.PhotoRealPending);
    }

    [Fact]
    public async Task ARefinishWaitingForARunner_GetsItsViewBack()
    {
        using var h = new WallTestHarness();
        using var s = RunnerSplatFlowTests.Scenario(h, out _);
        var (captureId, _) = await RunnerSplatFlowTests.FinishedAsync(h, s);
        Assert.Empty(await s.Service.RefinishPhotoRealAsync(captureId));
        await RunnerSplatFlowTests.ProcessNextAsync(s, captureId);

        Assert.True(await s.Runners!.CancelForCaptureAsync(captureId, Reason, CancellationToken.None));

        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
        var training = await db.GpuJobs.SingleAsync(j => j.Kind == GpuJobKind.Splat);
        Assert.Equal(GpuJobStatus.Succeeded, training.Status);
        Assert.NotNull(training.InstalledAt);
        Assert.Null(training.RefinishStateJson);
        Assert.Single(await db.WallGeometrySplats.ToListAsync());
        Assert.True((await s.Service.GetCaptureAsync(captureId))!.CanRefinish);
    }

    [Fact]
    public async Task AParkedReconstruction_CanBeCancelled_AndTheCaptureFailsKeepingItsPhotos()
    {
        using var h = new WallTestHarness();
        var clock = new MutableTestClock(DateTimeOffset.UtcNow);
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] }, runners: RunnerSplatFlowTests.OnRunners(), clock: clock);
        var captureId = await MarkerlessFixture.StartAsync(s);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await s.Runners!.SweepAsync(CancellationToken.None);
        Assert.Equal(
            "Reconstruction pending: waiting for a 3D runner to reconstruct the wall (none online)",
            (await s.Service.GetCaptureAsync(captureId))!.PhotoRealPending);

        Assert.True(await s.Runners.CancelForCaptureAsync(captureId, Reason, CancellationToken.None));
        await RunnerSplatFlowTests.ProcessNextAsync(s, captureId);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains(Reason, capture.Error);
        Assert.Equal(3, await db.WallCapturePhotos.CountAsync(p => p.CaptureId == captureId));
        Assert.Equal(GpuJobStatus.Cancelled, (await db.GpuJobs.SingleAsync()).Status);
    }
}
