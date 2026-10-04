// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Render textures again" routed to a 3D runner, end to end over the fake worker: the job is queued with the photos and the
/// options, a runner renders and uploads, and the textures are installed through the SAME path as a render on the host (rows,
/// masks, source maps, the placement re-run), or the failure ends the re-render with the old textures kept.
/// </summary>
public class GpuRunnerTexturesFlowTests
{
    [Fact]
    public async Task ARerenderOnARunner_QueuesTheJob_AndInstallsTheUploadedTexturesLikeAHostRender()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        using var s = new CaptureScenario(
            h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero },
            followUps: harness => FollowUpChains.Build(harness.RootContextFactory, new ScriptedFollowUpStep(PlaceHoldsFollowUpStep.StepKey, 100, log)));
        var captureId = await FinishedAsync(s);
        var runner = await TexturesJobSupport.AddTexturesRunnerAsync(h, s.Runners!);
        log.Clear();

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId, TextureQuality.High, TextureRoute.Runner));
        Assert.Equal(captureId, await s.TextureQueue.DequeueAsync(CancellationToken.None));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        // Queued, not rendered on the host: the capture still says it is rendering, on the job.
        var job = await QueuedJobAsync(h);
        var mark = (await CaptureAsync(h)).TexturesJobId;
        Assert.Equal((GpuJobKind.Textures, SplatQuality.Draft, true), (job.Kind, job.Quality, job.RequiredMemoryMb > 0));
        Assert.Equal(($"rerender:q1:gpu:{job.Id}", true, TextureQuality.High, job.Id.ToString()), (mark, CaptureTextureOutcome.RerenderOnRunner(mark), CaptureTextureOutcome.RerenderQuality(mark), CaptureTextureOutcome.RerenderJobId(mark)));
        Assert.Equal(1, s.Client.MultipartSubmissions.Count(m => m.Kind == "textures"));

        // The runner takes the bundle: the options of the quality, the geometry and the photos the model solved.
        var claimed = await s.Runners!.TryClaimAsync(runner, null, CancellationToken.None);
        Assert.Equal(job.Id, claimed?.Id);
        var (outcome, path) = await s.Runners.BundleForRunnerAsync(runner, job.Id, CancellationToken.None);
        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(path!))
        {
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("photos/p", StringComparison.Ordinal));
            using var options = new StreamReader(zip.GetEntry(RunnerTexturesBundle.JobFile)!.Open());
            Assert.Contains("\"mmPerPx\":1.5", await options.ReadToEndAsync());
        }

        // Upload: the capture's re-render is woken, and installs.
        Assert.Equal(
            RunnerJobOutcome.Ok,
            await s.Runners.AcceptResultAsync(runner, job.Id, new MemoryStream(TexturesJobSupport.ResultZip()), null, null, CancellationToken.None));
        Assert.Equal(captureId, await s.TextureQueue.DequeueAsync(CancellationToken.None));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var after = await CaptureAsync(h);
        Assert.False(CaptureTextureOutcome.IsRerendering(after.TexturesJobId));
        Assert.Equal(WallCaptureStatus.Succeeded, after.Status);
        await using var db = h.CreateContext();
        var texture = await db.WallGeometryTextures.SingleAsync(t => t.GeometryModelId == after.GeometryModelId);
        Assert.Equal(("0", 1550, 1300, true, true), (texture.FacetId, texture.WidthPx, texture.HeightPx, texture.MaskStoredPath is not null, texture.SourceMapStoredPath is not null));
        var done = await TexturesJobSupport.JobAsync(h, job.Id);
        Assert.NotNull(done.InstalledAt);
        Assert.Null(done.ResultPath);
        Assert.Equal([PlaceHoldsFollowUpStep.StepKey], log);
    }

    [Fact]
    public async Task AFailedRunnerJob_EndsTheRerender_AndKeepsThePreviousTextures()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        var captureId = await FinishedAsync(s);
        var runner = await TexturesJobSupport.AddTexturesRunnerAsync(h, s.Runners!);
        await s.Service.RerenderTexturesAsync(captureId, TextureQuality.Maximum, TextureRoute.Runner);
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);
        var job = await QueuedJobAsync(h);
        await s.Runners!.TryClaimAsync(runner, null, CancellationToken.None);

        await s.Runners.FailAsync(runner, job.Id, new RunnerFailure("out of memory", Retryable: false), CancellationToken.None);
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var after = await CaptureAsync(h);
        Assert.False(CaptureTextureOutcome.IsRerendering(after.TexturesJobId));
        Assert.Equal(WallCaptureStatus.Succeeded, after.Status);
        Assert.Contains("the previous textures stay", ((await CaptureFollowUpNoteAsync(h)) ?? string.Empty));
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallGeometryTextures.CountAsync(t => t.GeometryModelId == after.GeometryModelId));
        Assert.Equal(GpuJobStatus.Failed, (await TexturesJobSupport.JobAsync(h, job.Id)).Status);
    }

    [Fact]
    public async Task WhileTheJobWaits_TheRerenderLeavesTheMark_AndDoesNotUseUpItsStarts()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        var captureId = await FinishedAsync(s);
        await TexturesJobSupport.AddTexturesRunnerAsync(h, s.Runners!);
        await s.Service.RerenderTexturesAsync(captureId, TextureQuality.High, TextureRoute.Runner);

        for (var i = 0; i < WallCaptureProcessor.MaxRedoStarts + 2; i++)
        {
            await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);
        }

        Assert.True(await s.Processor.MayQueueRedoAsync(captureId, CaptureRedoKind.Rerender, CancellationToken.None));
        Assert.True(CaptureTextureOutcome.RerenderOnRunner((await CaptureAsync(h)).TexturesJobId));
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.GpuJobs.CountAsync());
    }

    [Fact]
    public async Task ARunnerRoute_WithNoCapableRunnerOnline_IsRefused_AndLeavesNoMark()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        var captureId = await FinishedAsync(s);

        var problems = await s.Service.RerenderTexturesAsync(captureId, TextureQuality.High, TextureRoute.Runner);

        Assert.Contains("No online 3D runner", Assert.Single(problems));
        Assert.False(CaptureTextureOutcome.IsRerendering((await CaptureAsync(h)).TexturesJobId));
    }

    [Fact]
    public async Task TheHostRoute_IsUnchanged_AndNeverQueuesAGpuJob()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        var captureId = await FinishedAsync(s);
        await TexturesJobSupport.AddTexturesRunnerAsync(h, s.Runners!);
        var before = s.Client.MultipartSubmissions.Count(m => m.Kind == "textures");

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId, TextureQuality.High));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        Assert.Equal(before + 1, s.Client.MultipartSubmissions.Count(m => m.Kind == "textures"));
        await using var db = h.CreateContext();
        Assert.Empty(await db.GpuJobs.ToListAsync());
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

    private static async Task<GpuJob> QueuedJobAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.AsNoTracking().SingleAsync();
    }

    private static async Task<string?> CaptureFollowUpNoteAsync(WallTestHarness h) =>
        CaptureFollowUpRecord.Parse((await CaptureAsync(h)).FollowUpJson).Note;
}
