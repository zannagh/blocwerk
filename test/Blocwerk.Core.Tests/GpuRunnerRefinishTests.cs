// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Finishing a capture's trained photo-real view again without training: the 3D runner's result stays on the server
/// after the install, and a re-finish runs the server half (splat-finish, install) and the whole post-capture chain
/// again from it. No GPU job is queued, no prepare runs; an installed preview whose final result failed can be
/// re-finished too.
/// </summary>
public class GpuRunnerRefinishTests
{
    [Fact]
    public async Task ARefinish_FinishesTheKeptResultAgain_AndRerunsTheChain_WithoutTraining()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.ProcessAsync();
        var installed = await SplatAsync(h);
        var job = await JobAsync(h);
        Assert.True(File.Exists(f.Scenario.Files.ResolvePhysicalPath(job.ResultPath!)));
        Assert.True((await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.CanRefinish);

        Assert.Empty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.Splatting, capture.Status);
            Assert.NotNull(capture.FollowUpJson); // cleared only once the re-finished view is installed
        }

        await f.ProcessAsync();

        var refinished = await SplatAsync(h);
        Assert.NotEqual(installed.Id, refinished.Id);
        Assert.Equal(2, f.Finishes());
        Assert.Single(f.Scenario.SplatClient.MultipartSubmissions, m => m.Kind == WallCaptureProcessor.PrepareKind);
        job = await JobAsync(h);
        Assert.Equal(GpuJobStatus.Succeeded, job.Status);
        Assert.NotNull(job.InstalledAt);
        Assert.True(File.Exists(f.Scenario.Files.ResolvePhysicalPath(job.ResultPath!)));
        Assert.Equal(WallCaptureStatus.Succeeded, (await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.Status);
        await f.PhotoRealStep.Received(1).RunAsync(Arg.Is<CaptureFollowUpContext>(c => c.SplatId == refinished.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnInstalledPreview_WhoseFinalResultFailed_CanBeRefinished()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(20000));
        await f.InstallPreviewAsync();
        await f.Runners.FailAsync(f.Runner, f.Job.Id, new RunnerFailure("device not ready", Retryable: false), CancellationToken.None);

        Assert.Empty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
        await f.ProcessAsync();

        var job = await JobAsync(h);
        Assert.Equal(GpuJobStatus.Succeeded, job.Status);
        Assert.NotNull(job.InstalledAt);
        Assert.Null(job.PreviewPath);
        Assert.Contains("\"previewStep\":20000", job.ResultStatsJson);
        var splat = await SplatAsync(h);
        await f.PhotoRealStep.Received(1).RunAsync(Arg.Is<CaptureFollowUpContext>(c => c.SplatId == splat.Id), Arg.Any<CancellationToken>());
        Assert.Equal(2, f.Finishes());
    }

    [Fact]
    public async Task AFailedFinish_KeepsTheResult_SoItCanBeFinishedAgain()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        var frame = f.Scenario.SplatClient.FrameJson;
        f.Scenario.SplatClient.FrameJson = "{ not json";
        await f.ProcessAsync();

        var job = await JobAsync(h);
        Assert.Equal(GpuJobStatus.Failed, job.Status);
        Assert.True(File.Exists(f.Scenario.Files.ResolvePhysicalPath(job.ResultPath!)));
        Assert.True((await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.CanRefinish);

        f.Scenario.SplatClient.FrameJson = frame;
        Assert.Empty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
        await f.ProcessAsync();

        Assert.NotNull((await JobAsync(h)).InstalledAt);
        Assert.NotNull(await SplatAsync(h));
    }

    [Fact]
    public async Task NoRefinish_WithoutAKeptResult_OrWhileTheRunnerStillTrains()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);

        var problems = await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId);
        Assert.Contains(problems, p => p.Contains("no trained photo-real view left", StringComparison.Ordinal));
        Assert.False((await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.CanRefinish);
        await using var db = h.CreateContext();
        Assert.Equal(GpuJobStatus.Claimed, (await db.GpuJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task ANewerInstall_DropsTheOlderJobsLeftover()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.ProcessAsync();
        var first = await JobAsync(h);
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(await f.Scenario.Service.RetrainPhotoRealAsync(f.CaptureId, SplatQuality.Max));
        await f.ProcessAsync();
        var second = await f.Runners.TryClaimAsync(f.Runner, null, CancellationToken.None);
        var upload = new MemoryStream(RunnerFixture.Gzip(RunnerFixture.SlimPly()));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Runners.AcceptResultAsync(f.Runner, second!.Id, upload, "gzip", null, CancellationToken.None));
        await f.ProcessAsync();

        await using var db = h.CreateContext();
        var old = await db.GpuJobs.AsNoTracking().SingleAsync(j => j.Id == first.Id);
        Assert.Null(old.ResultPath);
        Assert.False(File.Exists(f.Scenario.Files.ResolvePhysicalPath(first.ResultPath!)));
        Assert.NotNull((await db.GpuJobs.AsNoTracking().SingleAsync(j => j.Id == second.Id)).ResultPath);
    }

    private static async Task<WallGeometrySplat> SplatAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallGeometrySplats.AsNoTracking().SingleAsync();
    }

    private static async Task<GpuJob> JobAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).FirstAsync();
    }
}
