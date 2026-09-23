// <copyright file="CaptureVideoBusyGateTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Web.HealthChecks;
using Blocwerk.Web.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A walk-along video upload and its frame extraction hold the deploy gate: the autodeploy polls
/// <c>/health/ready-to-deploy</c> (the <c>busy</c> check) and would otherwise recreate the container
/// halfway through a 2 GB upload or an ffmpeg run. The gate must also open again on every way out —
/// success, refusal, failure and cancellation.
/// </summary>
public class CaptureVideoBusyGateTests
{
    [Fact]
    public async Task Upload_ReportsBusyWhileStreaming_AndIdleAfterwards()
    {
        var registry = new EditActivityRegistry();
        using var h = new WallTestHarness();
        using var s = Scenario(h, registry);
        var draft = await DraftAsync(s, h);
        var seen = new List<HealthStatus>();
        var kinds = new List<EditKind>();

        await using var body = new ObservingReadStream(FakeVideoFrameExtractor.Mp4Stream(), () =>
        {
            seen.Add(new BusyHealthCheck(registry).CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult().Status);
            kinds.AddRange(registry.Snapshot().Select(e => e.EditKind));
        });
        await s.Service.AddVideoAsync(draft, "walk.mov", body, CancellationToken.None);

        Assert.NotEmpty(seen);
        Assert.All(seen, status => Assert.Equal(HealthStatus.Degraded, status));
        Assert.All(kinds, kind => Assert.Equal(EditKind.CaptureVideoUpload, kind));
        Assert.False(registry.IsBusy());
    }

    [Fact]
    public async Task Upload_ReleasesTheGate_WhenRefusedForSize()
    {
        var registry = new EditActivityRegistry();
        using var h = new WallTestHarness();
        using var s = Scenario(h, registry, new WallCapturePipelineOptions { MaxVideoBytes = 1024 });
        var draft = await DraftAsync(s, h);
        var busyDuringRead = false;

        await using var body = new ObservingReadStream(FakeVideoFrameExtractor.Mp4Stream(4096), () => busyDuringRead |= registry.IsBusy());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.AddVideoAsync(draft, "walk.mp4", body, CancellationToken.None));

        Assert.True(busyDuringRead);
        Assert.False(registry.IsBusy());
    }

    [Fact]
    public async Task Upload_ReleasesTheGate_WhenTheRequestIsAborted()
    {
        var registry = new EditActivityRegistry();
        using var h = new WallTestHarness();
        using var s = Scenario(h, registry);
        var draft = await DraftAsync(s, h);
        using var abort = new CancellationTokenSource();

        await using var body = new ObservingReadStream(FakeVideoFrameExtractor.Mp4Stream(), () =>
        {
            Assert.True(registry.IsBusy());
            abort.Cancel();
            abort.Token.ThrowIfCancellationRequested();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Service.AddVideoAsync(draft, "walk.mp4", body, abort.Token));

        Assert.False(registry.IsBusy());
        Assert.Empty(s.Files.ListFiles());
    }

    [Fact]
    public async Task FrameExtraction_ReportsBusyWhileRunning_AndIdleAfterwards()
    {
        var registry = new EditActivityRegistry();
        using var h = new WallTestHarness();
        using var s = Scenario(h, registry);
        s.SplatClient.IsConfigured = true;
        var kinds = new List<EditKind>();
        s.Video.DuringExtract = () => kinds.AddRange(registry.Snapshot().Select(e => e.EditKind));
        var captureId = await s.StartCaptureAsync(photos: 2, beforeStart: id => UploadAsync(s, id));
        Assert.False(registry.IsBusy()); // the upload's hold is gone once it returned

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal([EditKind.CaptureVideoFrames], kinds);
        Assert.False(registry.IsBusy());
    }

    [Fact]
    public async Task FrameExtraction_ReleasesTheGate_WhenFfmpegFails()
    {
        var registry = new EditActivityRegistry();
        using var h = new WallTestHarness();
        using var s = Scenario(h, registry);
        s.SplatClient.IsConfigured = true;
        var busyDuringExtract = false;
        s.Video.DuringExtract = () => busyDuringExtract = registry.IsBusy();
        s.Video.Failure = new InvalidDataException("ffmpeg failed (1): moov atom not found");
        var captureId = await s.StartCaptureAsync(photos: 2, beforeStart: id => UploadAsync(s, id));

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.True(busyDuringExtract);
        Assert.False(registry.IsBusy());
    }

    [Fact]
    public async Task FrameExtraction_ReleasesTheGate_OnShutdown()
    {
        var registry = new EditActivityRegistry();
        using var h = new WallTestHarness();
        using var s = Scenario(h, registry);
        s.SplatClient.IsConfigured = true;
        using var stopping = new CancellationTokenSource();
        var busyDuringExtract = false;
        s.Video.DuringExtract = () =>
        {
            busyDuringExtract = registry.IsBusy();
            stopping.Cancel();
            stopping.Token.ThrowIfCancellationRequested();
        };
        var captureId = await s.StartCaptureAsync(photos: 2, beforeStart: id => UploadAsync(s, id));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Processor.ProcessAsync(captureId, stopping.Token));

        Assert.True(busyDuringExtract);
        Assert.False(registry.IsBusy());
    }

    private static CaptureScenario Scenario(WallTestHarness h, EditActivityRegistry registry, WallCapturePipelineOptions? options = null) =>
        new(h, options: options, busyGate: new EditActivityDeployBusyGate(registry));

    private static async Task<Guid> DraftAsync(CaptureScenario s, WallTestHarness h)
    {
        s.SplatClient.IsConfigured = true;
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        return (await s.Service.CreateDraftAsync(h.WallId)).CaptureId;
    }

    private static async Task UploadAsync(CaptureScenario s, Guid captureId)
    {
        await using var video = FakeVideoFrameExtractor.Mp4Stream();
        await s.Service.AddVideoAsync(captureId, "walk.mov", video, CancellationToken.None);
    }
}
