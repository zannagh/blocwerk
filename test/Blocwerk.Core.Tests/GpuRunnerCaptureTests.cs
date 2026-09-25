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
/// The capture side of the runners and the no-GPU rule: a capture never waits for a runner. With a runner the
/// photo-real stage is prepared on the server and the capture completes as "Model ready" at once; the view is
/// installed (and the post-capture chain's photo-real steps run again) whenever a runner delivers it. Without a
/// runner, or with a splat worker that cannot split, everything behaves as before.
/// </summary>
public class GpuRunnerCaptureTests
{
    private static readonly string[] SplitKinds = ["splat", WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind];

    private readonly ICaptureFollowUpStep photoRealStep = PhotoRealStep();

    [Fact]
    public async Task OnlineRunner_CaptureCompletesAtOnce_ThenTheViewIsInstalled_AndThePhotoRealStepsRunAgain()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out var clock);
        var captureId = await s.StartCaptureAsync();
        var (runner, _) = await AddRunnerAsync(h, clock);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal([WallCaptureProcessor.PrepareKind], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.False(summary.IsRunning);
        Assert.Null(summary.Error);
        Assert.StartsWith("Photo-real view pending", summary.PhotoRealPending);
        await photoRealStep.Received(1).RunAsync(Arg.Is<CaptureFollowUpContext>(c => c.SplatId == null), Arg.Any<CancellationToken>());

        var job = await s.Runners!.TryClaimAsync(runner, null, CancellationToken.None);
        Assert.NotNull(job);
        var upload = new MemoryStream(RunnerFixture.Gzip(RunnerFixture.SlimPly()));
        Assert.Equal(RunnerJobOutcome.Ok, await s.Runners.AcceptResultAsync(runner, job.Id, upload, "gzip", null, CancellationToken.None));
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal([WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        var splat = await db.WallGeometrySplats.SingleAsync();
        Assert.Equal(capture.GeometryModelId, splat.GeometryModelId);
        Assert.NotNull((await db.GpuJobs.SingleAsync()).InstalledAt);
        Assert.Null((await s.Service.GetCaptureAsync(captureId))!.PhotoRealPending);
        await photoRealStep.Received(1).RunAsync(Arg.Is<CaptureFollowUpContext>(c => c.SplatId == splat.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoMode_WithoutAnOnlineRunner_KeepsTheAllInOnePath()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out var clock);
        var captureId = await s.StartCaptureAsync();
        await AddRunnerAsync(h, clock, online: false);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal(["splat"], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
        Assert.Empty(await db.GpuJobs.ToListAsync());
    }

    [Fact]
    public async Task AlwaysMode_WithNoRunnerAtAll_CompletesAsModelReady_AndTheViewWaitsQuietly()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out var clock, GpuRunnerMode.Always);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await s.Runners!.SweepAsync(CancellationToken.None);

        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.Error);
        Assert.Null(summary.FollowUpNote);
        Assert.Equal("Photo-real view pending: waiting for a 3D runner that can train high (none online)", summary.PhotoRealPending);
        Assert.Equal([WallCaptureProcessor.PrepareKind], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
    }

    [Fact]
    public async Task AWorkerWithoutTheSplitKinds_TrainsItselfAsBefore_EvenInAlwaysMode()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out _, GpuRunnerMode.Always, split: false);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal(["splat"], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
        Assert.Single(await db.WallGeometrySplats.ToListAsync());
    }

    [Fact]
    public async Task WithoutASplatWorker_NothingWaits_EvenInAlwaysMode()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out _, GpuRunnerMode.Always);
        s.SplatClient.IsConfigured = false;
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.PhotoRealPending);
        await using var db = h.CreateContext();
        Assert.Empty(await db.GpuJobs.ToListAsync());
    }

    [Fact]
    public async Task Cancelling_KeepsTheFinishedCapture_AndARetrainSupersedesAWaitingJob()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out _, GpuRunnerMode.Always);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.True(await s.Runners!.CancelForCaptureAsync(captureId, "cancelled by an admin", CancellationToken.None));
        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.PhotoRealPending);

        Assert.Empty(await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.Max));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        Assert.Empty(await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.Draft));

        await using var db = h.CreateContext();
        var jobs = await db.GpuJobs.OrderBy(j => j.CreatedAt).ToListAsync();
        Assert.Equal([GpuJobStatus.Cancelled, GpuJobStatus.Cancelled], jobs.Select(j => j.Status));
        Assert.Equal("superseded by a retrain", jobs[1].Error);
        Assert.Equal(WallCaptureStatus.Splatting, (await db.WallCaptures.SingleAsync()).Status);
    }

    private static ICaptureFollowUpStep PhotoRealStep()
    {
        var step = Substitute.For<ICaptureFollowUpStep>();
        step.Key.Returns("photo-real-probe");
        step.Title.Returns("Measuring in the photo-real view");
        step.NeedsPhotoReal.Returns(true);
        step.RunAsync(Arg.Any<CaptureFollowUpContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaptureFollowUpStepResult(CaptureFollowUpOutcome.Done, "measured"));
        return step;
    }

    private static async Task<(GpuRunner Runner, string Token)> AddRunnerAsync(WallTestHarness h, MutableTestClock clock, bool online = true)
    {
        var (token, prefix) = GpuRunnerTokens.Create();
        var runner = new GpuRunner
        {
            Name = "gpu", OwnerUserId = h.Owner.Id, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix,
            LastSeenAt = online ? clock.GetUtcNow() : null, MaxQuality = "max",
        };
        await using var db = h.CreateContext();
        db.GpuRunners.Add(runner);
        db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = h.WallId });
        await db.SaveChangesAsync();
        return (runner, token);
    }

    private CaptureScenario Scenario(
        WallTestHarness h, out MutableTestClock clock, GpuRunnerMode mode = GpuRunnerMode.Auto, bool split = true)
    {
        clock = new MutableTestClock(DateTimeOffset.UtcNow);
        var s = new CaptureScenario(
            h,
            followUps: harness => FollowUpChains.Build(harness.RootContextFactory, photoRealStep),
            runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, Mode = mode },
            clock: clock);
        s.SplatClient.IsConfigured = true;
        s.SplatClient.Kinds = split ? [.. SplitKinds] : ["splat"];
        s.SplatClient.Download = name => name switch
        {
            "bundle.zip" => RunnerFixture.Bundle(),
            "prepared.json" => "{\"version\":1}"u8.ToArray(),
            "frame.json" => System.Text.Encoding.UTF8.GetBytes(s.SplatClient.FrameJson),
            "wall.spz" => s.SplatClient.Spz,
            _ => CaptureScenario.TinyJpeg(),
        };
        return s;
    }
}
