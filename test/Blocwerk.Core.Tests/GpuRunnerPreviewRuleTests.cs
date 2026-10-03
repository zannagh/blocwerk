// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The edges of previews, leftovers and re-finishes: a pending preview dies with its job and never lands over a newer
/// view, an installed preview's file is never deleted by a newer upload, a final install clears the preview state, a
/// failed re-finish puts job and capture back, a server training drops the runner leftover, and the re-finish offer
/// needs the kept file on disk.
/// </summary>
public class GpuRunnerPreviewRuleTests
{
    private static readonly RunnerFailure Fatal = new("CUDA driver error: device not ready", Retryable: false);

    [Fact]
    public async Task APendingPreview_GoesWithAFatalFailure_AndNeverInstallsAfterIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));
        var pending = (await JobAsync(h)).PreviewPath!;

        await f.Runners.FailAsync(f.Runner, f.Job.Id, Fatal, CancellationToken.None);
        Assert.False(await f.Runners.TryMarkPreviewInstalledAsync(f.Job.Id, 7000, pending, CancellationToken.None));
        await f.InstallPreviewAsync();

        var job = await JobAsync(h);
        Assert.Null(job.PreviewPath);
        Assert.False(File.Exists(f.Scenario.Files.ResolvePhysicalPath(pending)));
        Assert.Null(await SplatAsync(h));
        var summary = (await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!;
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, summary.Status);
        Assert.False(summary.CanRefinish);
    }

    [Fact]
    public async Task APendingPreview_NeverInstallsOverAViewInstalledSinceItArrived()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));
        var newer = new WallGeometrySplat { GeometryModelId = f.Job.GeometryModelId, StoredPath = "newer.spz", FrameJson = "{}" };
        await using (var db = h.CreateContext())
        {
            db.WallGeometrySplats.Add(newer);
            await db.SaveChangesAsync();
        }

        await f.InstallPreviewAsync();

        Assert.Equal(newer.Id, (await SplatAsync(h))!.Id);
        Assert.Null((await JobAsync(h)).PreviewInstalledStep);
    }

    [Fact]
    public async Task ANewerPreview_NeverDeletesTheInstalledOnesFile_AndTheInstalledOneStaysReusable()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));
        await f.InstallPreviewAsync();
        var installed = (await JobAsync(h)).InstalledPreviewPath!;

        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(20000));
        var pending = (await JobAsync(h)).PreviewPath!;
        await f.Runners.DropPreviewAsync(f.Job.Id, 20000, "not-the-pending-one", CancellationToken.None);
        Assert.Equal(pending, (await JobAsync(h)).PreviewPath);
        Assert.True(File.Exists(f.Scenario.Files.ResolvePhysicalPath(installed)));

        await f.Runners.FailAsync(f.Runner, f.Job.Id, Fatal, CancellationToken.None);
        Assert.False(File.Exists(f.Scenario.Files.ResolvePhysicalPath(pending)));
        Assert.True(File.Exists(f.Scenario.Files.ResolvePhysicalPath(installed)));
        Assert.Empty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
        await f.ProcessAsync();
        Assert.NotNull((await JobAsync(h)).InstalledAt);
    }

    [Fact]
    public async Task AFailedRefinish_RestoresJobAndCapture_WhileTheViewBeforeStays()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));
        await f.InstallPreviewAsync();
        var preview = (await JobAsync(h)).InstalledPreviewPath!;
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.ProcessAsync();
        var view = (await SplatAsync(h))!.Id;
        var installed = await JobAsync(h);
        Assert.Null(installed.PreviewInstalledStep);
        Assert.Null(installed.InstalledPreviewPath);
        Assert.False(File.Exists(f.Scenario.Files.ResolvePhysicalPath(preview)));

        f.Scenario.SplatClient.FrameJson = "{ not json";
        Assert.Empty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
        await f.ProcessAsync();

        var summary = (await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.Error);
        Assert.StartsWith("Finishing the trained photo-real view again failed", summary.FollowUpNote);
        Assert.True(summary.CanRefinish);
        var job = await JobAsync(h);
        Assert.Equal(GpuJobStatus.Succeeded, job.Status);
        Assert.NotNull(job.InstalledAt);
        Assert.Null(job.RefinishStateJson);
        Assert.Equal(view, (await SplatAsync(h))!.Id);
    }

    [Fact]
    public async Task AServerTraining_DropsTheRunnerLeftover_SoNoRefinishBringsTheOlderViewBack()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.ProcessAsync();
        var result = (await JobAsync(h)).ResultPath!;

        f.Scenario.SplatClient.Kinds = ["splat"];
        Assert.Empty(await f.Scenario.Service.RetrainPhotoRealAsync(f.CaptureId, SplatQuality.Max));
        await f.ProcessAsync();

        Assert.Null((await JobAsync(h)).ResultPath);
        Assert.False(File.Exists(f.Scenario.Files.ResolvePhysicalPath(result)));
        Assert.False((await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.CanRefinish);
        Assert.NotEmpty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
    }

    [Fact]
    public async Task TheRefinishOffer_NeedsTheKeptFileOnDisk()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await f.ResultAsync());
        await f.ProcessAsync();
        Assert.True((await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.CanRefinish);

        File.Delete(f.Scenario.Files.ResolvePhysicalPath((await JobAsync(h)).ResultPath!)!);

        Assert.False((await f.Scenario.Service.GetCaptureAsync(f.CaptureId))!.CanRefinish);
        Assert.NotEmpty(await f.Scenario.Service.RefinishPhotoRealAsync(f.CaptureId));
    }

    private static async Task<WallGeometrySplat?> SplatAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallGeometrySplats.AsNoTracking().SingleOrDefaultAsync();
    }

    private static async Task<GpuJob> JobAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).FirstAsync();
    }
}
