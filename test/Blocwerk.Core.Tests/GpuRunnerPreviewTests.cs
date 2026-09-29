// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Photo-real previews from a training 3D runner: installed on the model while the capture stays "Model ready", only
/// ever moving forward, never over the final result, and kept (and said so) when the final result fails for good. The
/// post-capture chain runs on the final view only.
/// </summary>
public class GpuRunnerPreviewTests
{
    [Fact]
    public async Task APreview_IsInstalledWhileTraining_AndOnlyANewerOneReplacesIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);

        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));
        await f.InstallPreviewAsync();

        var first = await SplatAsync(h);
        Assert.NotNull(first);
        var summary = (await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.StartsWith("Photo-real preview (step 7000 of 50000) — refining: 3D runner “gpu”", summary.PhotoRealPending);

        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(7000));
        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(5000));
        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(50000));
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(20000));
        await f.InstallPreviewAsync();

        var second = await SplatAsync(h);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Equal(20000, (await JobAsync(h)).PreviewInstalledStep);
        Assert.False(await f.Runners.TryMarkPreviewInstalledAsync(f.Job.Id, 7000, "any", CancellationToken.None));

        // The chain ran once, when the capture completed (no view yet); previews never run it.
        await f.PhotoRealStep.Received(1).RunAsync(Arg.Any<CaptureFollowUpContext>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, f.Finishes());
    }

    [Fact]
    public async Task TheFinalResult_SupersedesAPendingPreview_AndPreviewsAfterItAreRefused()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));

        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.InstallPreviewAsync();
        Assert.Null(await SplatAsync(h));
        Assert.False(await f.Runners.TryMarkPreviewInstalledAsync(f.Job.Id, 7000, "any", CancellationToken.None));

        await f.ProcessAsync();

        var job = await JobAsync(h);
        Assert.NotNull(job.InstalledAt);
        Assert.Null(job.PreviewPath);
        Assert.Null(job.PreviewInstalledStep);
        var splat = await SplatAsync(h);
        Assert.NotNull(splat);
        Assert.NotEqual(RunnerJobOutcome.Ok, await f.PreviewAsync(20000));
        await f.PhotoRealStep.Received(1).RunAsync(Arg.Is<CaptureFollowUpContext>(c => c.SplatId == splat!.Id), Arg.Any<CancellationToken>());
        Assert.Null((await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.PhotoRealPending);
    }

    [Fact]
    public async Task AFinalResultThatFailsForGood_KeepsTheInstalledPreview_AndTheCaptureSaysSo()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(20000));
        await f.InstallPreviewAsync();
        var preview = await SplatAsync(h);

        var failed = new RunnerFailure("CUDA driver error: device not ready", Retryable: false);
        Assert.Equal(RunnerJobOutcome.Ok, await f.Runners.FailAsync(f.Runner, f.Job.Id, failed, CancellationToken.None));

        var summary = (await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.Error);
        Assert.StartsWith("Photo-real preview (step 20000 of 50000) kept: the final photo-real view could not be made", summary.FollowUpNote);
        Assert.True(summary.CanRefinish);
        Assert.Equal(preview!.Id, (await SplatAsync(h))!.Id);
        var job = await JobAsync(h);
        Assert.Equal(GpuJobStatus.Failed, job.Status);
        Assert.Null(job.PreviewPath);
        Assert.True(File.Exists(f.Scenario.Files.ResolvePhysicalPath(job.InstalledPreviewPath!)));
        Assert.False(File.Exists(f.Scenario.Files.ResolvePhysicalPath(job.BundlePath)));
    }

    [Fact]
    public async Task PreviewsAreRefused_WhenTheServerHasThemOff()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(
            h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Always, Previews = false });

        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(7000));
        Assert.Null((await JobAsync(h)).PreviewPath);
    }

    [Fact]
    public void TheRules_OnlyMoveForward_AndNeverOverTheFinalResult()
    {
        var job = new GpuJob { BundlePath = "b", BundleSha256 = "s", PreparedPath = "p", Status = GpuJobStatus.Running };
        Assert.True(GpuJobPreviews.MayAccept(job, 7000, 50000));
        Assert.False(GpuJobPreviews.MayAccept(job, 50000, 50000));
        job.PreviewStep = 7000;
        job.PreviewInstalledStep = 7000;
        Assert.False(GpuJobPreviews.MayAccept(job, 7000, 50000));
        Assert.False(GpuJobPreviews.MayInstall(job, 7000));
        Assert.True(GpuJobPreviews.MayInstall(job, 20000));
        job.Status = GpuJobStatus.Queued;
        Assert.True(GpuJobPreviews.MayInstall(job, 20000));
        Assert.False(GpuJobPreviews.MayAccept(job, 20000, 50000));
        job.Status = GpuJobStatus.Failed;
        Assert.False(GpuJobPreviews.MayInstall(job, 20000));
        job.Status = GpuJobStatus.Succeeded;
        Assert.False(GpuJobPreviews.MayInstall(job, 20000));
        job.Status = GpuJobStatus.Cancelled;
        Assert.False(GpuJobPreviews.MayInstall(job, 20000));
        Assert.Equal(
            "Photo-real preview (step 7000 of 50000) — refining: waiting for a 3D runner (retrying)",
            GpuJobText.Pending(GpuJobStatus.Queued, "waiting for a 3D runner (retrying)", 0, null, 7000, 50000));
        Assert.Equal(
            "Photo-real view pending: trained, finishing on the server", GpuJobText.Pending(GpuJobStatus.Succeeded, null, 1, null));
    }

    private static async Task<WallGeometrySplat?> SplatAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallGeometrySplats.AsNoTracking().SingleOrDefaultAsync();
    }

    private static async Task<GpuJob> JobAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.AsNoTracking().SingleAsync();
    }
}
