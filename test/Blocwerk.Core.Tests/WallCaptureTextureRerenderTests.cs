// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Render wall textures again": only a textures job for the capture's active model, beside the capture worker. The
/// capture's status and photo-real state are never touched (also while the view trains); only the texture half of its
/// error changes, and the holds are placed on the new textures again.
/// </summary>
public class WallCaptureTextureRerenderTests
{
    private static readonly ComputeJobStatus NoCoverage = new() { Status = ComputeJobStates.Failed, Error = "no coverage" };

    [Fact]
    public async Task Rerender_StoresTheTextures_AndDropsOnlyTheTextureHalfOfTheError()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        using var s = new CaptureScenario(
            h, followUps: harness => FollowUpChains.Build(harness.RootContextFactory, new ScriptedFollowUpStep(PlaceHoldsFollowUpStep.StepKey, 100, log)));
        s.SplatClient.IsConfigured = true;
        s.Client.Terminal["textures"] = NoCoverage;
        s.SplatClient.Terminal["splat"] = new ComputeJobStatus { Status = ComputeJobStates.Failed, Error = "out of memory" };
        var captureId = await FinishedAsync(s);
        var before = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.SucceededWithoutTextures, before.Status);
        Assert.True((await s.Service.GetCaptureAsync(captureId))!.CanRerenderTextures);
        s.Client.Terminal.Remove("textures");

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId));
        var queued = await CaptureAsync(h);
        Assert.Equal((before.Status, before.Stage, before.Error), (queued.Status, queued.Stage, queued.Error));
        Assert.True((await s.Service.GetCaptureAsync(captureId))!.TexturesRerendering);
        Assert.Equal(captureId, await s.TextureQueue.DequeueAsync(CancellationToken.None));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var after = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, after.Status);
        Assert.StartsWith(WallCaptureService.SplatErrorStart, after.Error);
        Assert.Contains("out of memory", after.Error);
        Assert.Equal(before.SplatJobId, after.SplatJobId);
        Assert.False(CaptureTextureOutcome.IsRerendering(after.TexturesJobId));
        Assert.Equal("textures", s.Client.MultipartSubmissions[^1].Kind);
        Assert.Single(s.SplatClient.MultipartSubmissions);
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallGeometryTextures.CountAsync(t => t.GeometryModelId == after.GeometryModelId));
        Assert.Equal([PlaceHoldsFollowUpStep.StepKey, PlaceHoldsFollowUpStep.StepKey], log);
    }

    [Fact]
    public async Task Rerender_WhileThePhotoRealStageRuns_LeavesIt_AndTheStageCompletesWithTheNewTextures()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.Terminal["textures"] = NoCoverage;
        var captureId = await FinishedAsync(s);
        s.Client.Terminal.Remove("textures");
        s.SplatClient.IsConfigured = true;
        var splatJob = s.SplatClient.Adopt("splat");
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            capture.Status = WallCaptureStatus.Splatting;
            capture.Stage = "Photo-real view: training";
            capture.SplatJobId = splatJob;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var during = await CaptureAsync(h);
        Assert.Equal((WallCaptureStatus.Splatting, "Photo-real view: training", splatJob), (during.Status, during.Stage, during.SplatJobId));
        Assert.Null(during.Error);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var done = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.Succeeded, done.Status);
        Assert.Null(done.Error);
    }

    [Fact]
    public async Task FailedRerender_KeepsTheView_AndRecordsTheNewError()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.Client.Terminal["textures"] = NoCoverage;
        var captureId = await FinishedAsync(s);
        s.Client.Terminal["textures"] = new ComputeJobStatus { Status = ComputeJobStates.Failed, Error = "still too dark" };

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var capture = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.SucceededWithoutTextures, capture.Status);
        Assert.Contains("textures could not be made", capture.Error);
        Assert.Contains("still too dark", capture.Error);
        Assert.DoesNotContain("no coverage", capture.Error);
        Assert.Null(capture.TexturesJobId);
        await using var db = h.CreateContext();
        Assert.NotNull(await db.WallGeometrySplats.SingleOrDefaultAsync());
        Assert.Empty(await db.WallGeometryTextures.ToListAsync());
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheTexturesItHad_WithANote()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await FinishedAsync(s);
        s.Client.Terminal["textures"] = NoCoverage;

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.Error);
        Assert.Contains("the previous textures stay", summary.FollowUpNote);
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallGeometryTextures.CountAsync());
    }

    [Fact]
    public async Task Rerender_IsRefused_ForANonAdmin_WhileRunning_AndWithoutAnActiveModel()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        Assert.Contains(await s.Service.RerenderTexturesAsync(captureId), p => p.StartsWith("Only a capture whose 3D model is ready", StringComparison.Ordinal));

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Contains("This capture's 3D model is not the wall's active one.", await s.Service.RerenderTexturesAsync(captureId));
        Assert.False((await s.Service.GetCaptureAsync(captureId))!.CanRerenderTextures);

        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.RerenderTexturesAsync(captureId));
        Assert.False(CaptureTextureOutcome.IsRerendering((await CaptureAsync(h)).TexturesJobId));
    }

    [Fact]
    public async Task Rerender_QueuesNoGpuJob_AndNoCaptureRun()
    {
        using var h = new WallTestHarness();
        using var s = RunnerScenario(h);
        s.Client.Terminal["textures"] = NoCoverage;
        var captureId = await FinishedAsync(s);
        GpuJob job;
        await using (var db = h.CreateContext())
        {
            job = await db.GpuJobs.AsNoTracking().SingleAsync();
        }

        s.Client.Terminal.Remove("textures");
        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        await using (var db = h.CreateContext())
        {
            var after = await db.GpuJobs.AsNoTracking().SingleAsync();
            Assert.Equal((job.Id, job.Status), (after.Id, after.Status));
            Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
        }

        Assert.Equal([WallCaptureProcessor.PrepareKind], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        using var none = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await s.Queue.DequeueAsync(none.Token));
    }

    /// <summary>A started and processed capture; the start's entry is taken off the capture queue.</summary>
    private static async Task<Guid> FinishedAsync(CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        return captureId;
    }

    private static async Task<WallCapture> CaptureAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.AsNoTracking().SingleAsync();
    }

    /// <summary>Photo-real views go to 3D runners (none online): the capture completes with its GPU job queued.</summary>
    private static CaptureScenario RunnerScenario(WallTestHarness h)
    {
        var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Always });
        s.SplatClient.IsConfigured = true;
        s.SplatClient.Kinds = ["splat", WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind];
        s.SplatClient.Download = name => name switch
        {
            "bundle.zip" => RunnerFixture.Bundle(),
            "prepared.json" => "{\"version\":1}"u8.ToArray(),
            _ => CaptureScenario.TinyJpeg(),
        };
        return s;
    }
}
