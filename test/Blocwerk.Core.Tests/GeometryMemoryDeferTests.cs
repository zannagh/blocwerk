// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The geometry worker's per-job memory guard (<c>failureKind: resource</c>) defers the work instead of failing it: textures go to a
/// 3D runner, a solve waits for the next attempt; and the routing reads the worker's reported blend budget.
/// </summary>
public class GeometryMemoryDeferTests
{
    private static readonly ComputeJobStatus TooBig = new()
    {
        Status = ComputeJobStates.Failed, FailureKind = ComputeJobStates.ResourceFailure, Error = "rendering: needs more memory than this server allows for one job (1280 MB)",
    };

    [Fact]
    public async Task TexturesTooBigForTheHost_GoToARunner_AndTheCaptureSaysTheyWill()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        s.Client.Terminal["textures"] = TooBig;

        var captureId = await FinishedAsync(s);

        var done = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.SucceededWithoutTextures, done.Status);
        Assert.Equal(WallCaptureProcessor.TexturesDeferredText, done.Error);
        Assert.Contains(CaptureNeedsMoreMemoryException.WaitingText, done.Error);
        Assert.True(CaptureTextureOutcome.RerenderOnRunner(done.TexturesJobId));

        // The re-render worker picks the mark up and queues the textures for a runner (none needs to be online).
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);
        await using var db = h.CreateContext();
        var job = await db.GpuJobs.AsNoTracking().SingleAsync();
        Assert.Equal((GpuJobKind.Textures, captureId, GpuJobStatus.Queued), (job.Kind, job.CaptureId, job.Status));
        Assert.Equal(job.Id.ToString(), CaptureTextureOutcome.RerenderJobId((await CaptureAsync(h)).TexturesJobId));
        Assert.Equal(1, s.Client.MultipartSubmissions.Count(m => m.Kind == "textures"));
    }

    [Fact]
    public async Task AHostRerenderTooBigForTheHost_GoesToARunner_AndKeepsTheOldTextures()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        var captureId = await FinishedAsync(s);
        s.Client.Terminal["textures"] = TooBig;

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId, TextureQuality.High));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var capture = await CaptureAsync(h);
        Assert.Equal((WallCaptureStatus.Succeeded, null), (capture.Status, capture.Error));
        await using var db = h.CreateContext();
        var job = await db.GpuJobs.AsNoTracking().SingleAsync();
        Assert.Equal($"rerender:q1:gpu:{job.Id}", capture.TexturesJobId);
        Assert.True(await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == capture.GeometryModelId));
    }

    [Fact]
    public async Task TexturesTooBig_WithRunnersOff_EndWithoutTextures_AndSayWhy()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { Mode = GpuRunnerMode.Off });
        s.Client.Terminal["textures"] = TooBig;

        await FinishedAsync(s);

        var done = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.SucceededWithoutTextures, done.Status);
        Assert.Contains("needs more memory than this server has", done.Error);
        Assert.False(CaptureTextureOutcome.IsRerendering(done.TexturesJobId));
    }

    [Fact]
    public async Task ASolveTooBigForTheHost_WaitsForTheNextAttempt_InsteadOfFailing()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        s.Client.Terminal["solve"] = TooBig;

        var captureId = await FinishedAsync(s);

        var parked = await CaptureAsync(h);
        Assert.Equal((WallCaptureStatus.Solving, 0, null, null), (parked.Status, parked.Attempts, parked.SolveJobId, parked.GeometryModelId));
        Assert.EndsWith(CaptureNeedsMoreMemoryException.RetryText, parked.Stage);
        Assert.Null(parked.Error);

        // The next attempt (the worker resumes unfinished captures on start) submits the solve again.
        s.Client.Terminal.Remove("solve");
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var done = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.Succeeded, done.Status);
        Assert.Equal(2, s.Client.JsonSubmissions.Count(j => j.Kind == "solve"));
    }

    [Fact]
    public async Task ASolveTooBigForTheHost_WithRunnersOff_FailsWithTheReason()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { Mode = GpuRunnerMode.Off });
        s.Client.Terminal["solve"] = TooBig;

        await FinishedAsync(s);

        var failed = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.Failed, failed.Status);
        Assert.Contains("needs more memory than this server has", failed.Error);
        Assert.DoesNotContain("runner", failed.Error);
    }

    [Fact]
    public async Task ASolveThatKeepsRunningOutOfMemory_FailsOnceItHasWaitedADay()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        s.Client.Terminal["solve"] = TooBig;
        var captureId = await FinishedAsync(s);
        Assert.Equal(WallCaptureStatus.Solving, (await CaptureAsync(h)).Status);
        await using (var db = h.CreateContext())
        {
            var longAgo = DateTimeOffset.UtcNow - WallCaptureProcessor.SolveParkLifetime - TimeSpan.FromMinutes(1);
            await db.WallCaptures.Where(c => c.Id == captureId).ExecuteUpdateAsync(u => u.SetProperty(c => c.StartedAt, longAgo));
        }

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var failed = await CaptureAsync(h);
        Assert.Equal(WallCaptureStatus.Failed, failed.Status);
        Assert.Contains("after retrying for 24 hours", failed.Error);
        await using var check = h.CreateContext();
        Assert.True(await check.WallCapturePhotos.AnyAsync(p => p.CaptureId == captureId));
    }

    [Fact]
    public async Task TheRouting_FollowsTheBlendBudgetTheWorkerReports()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        var captureId = await FinishedAsync(s);
        await TexturesJobSupport.AddTexturesRunnerAsync(h, s.Runners!, "Mac", memoryMb: 48000);

        var unreported = await s.Service.GetTextureRoutingAsync(captureId);
        s.Client.TexturesBlendMaxBytes = 1_000_000;
        var small = await s.Service.GetTextureRoutingAsync(captureId);
        var estimates = await s.Service.EstimateTextureQualitiesAsync(captureId);

        Assert.All(unreported.Routes, r => Assert.Equal(TextureRoute.Host, r.Route));
        Assert.All(small.Estimates, e => Assert.NotEqual(TextureBlendFit.Full, e.Fit));
        Assert.All(small.Routes, r => Assert.Equal((TextureRoute.Runner, "Mac"), (r.Route, r.Runner?.Name)));
        Assert.All(estimates, e => Assert.NotEqual(TextureBlendFit.Full, e.Fit));
    }

    private static async Task<Guid> FinishedAsync(CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        await s.Queue.DequeueAsync(CancellationToken.None);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        return captureId;
    }

    private static async Task<WallCapture> CaptureAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.AsNoTracking().SingleAsync();
    }
}
