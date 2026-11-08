// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Recognition.Remote;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The app's recognition calls through the queue: a runner's answer is read back into the in-process types, a failed or
/// unclaimed job says "recognition unavailable" in <c>queue</c> mode and runs in the app in <c>queue+fallback</c> mode,
/// and a call outside a wall scope never queues anything.
/// </summary>
public sealed class RecognitionRemoteClientTests : IDisposable
{
    private const string MarkersResult = """
        {"version":1,"imageWidth":64,"imageHeight":48,"markers":[{"id":7,"cornersPx":[[0,0],[8,0],[8,8],[0,8]],
        "cornersNormalized":[[0,0],[0.125,0],[0.125,0.17],[0,0.17]],"sidePx":8,"edgeRatio":1,"synthetic":false}],
        "rejected":[],"undecodedCandidateCount":0,"suspicious":false,"warnings":[]}
        """;

    private static readonly MarkerDetectionResult InApp = new() { ImageWidth = 1, ImageHeight = 1, Markers = [], Rejected = [] };
    private readonly string modelFile = Path.Combine(Path.GetTempPath(), $"holds-model-{Guid.NewGuid():N}.onnx");
    private readonly IMarkerDetectionService inProcessMarkers = Substitute.For<IMarkerDetectionService>();

    public RecognitionRemoteClientTests()
    {
        File.WriteAllBytes(modelFile, [1, 2, 3, 4]);
        inProcessMarkers.DetectAsync(default!, default, default).ReturnsForAnyArgs(InApp);
    }

    public void Dispose() => File.Delete(modelFile);

    [Fact]
    public async Task Markers_ARunnerAnswers_ItsResultIsRead_AndTheAppDoesNotRun()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var call = new QueuedMarkerDetectionService(inProcessMarkers, FakeRecognitionClaimant.Caller(f))
            .DetectAsync(CaptureScenario.TinyJpeg(), null, CancellationToken.None);
        var job = await pod.ClaimAsync();
        await pod.CompleteAsync(job, MarkersResult);
        var result = await call;

        Assert.Equal(7, Assert.Single(result.Markers).Id);
        Assert.Equal((RecognitionKinds.Markers, RecognitionLane.Interactive, (int?)300), (job.Kind, job.Lane, job.RequiredMemoryMb));
        await inProcessMarkers.DidNotReceiveWithAnyArgs().DetectAsync(default!, default, default);
    }

    [Fact]
    public async Task Presence_ARunnerFails_InQueueMode_RecognitionIsUnavailable()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        var inProcess = Substitute.For<IHoldPresenceProbe>();
        var probe = new QueuedHoldPresenceProbe(inProcess, FakeRecognitionClaimant.Caller(f));
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var call = Task.Run(() => probe.Score(CaptureScenario.TinyJpeg(1), CaptureScenario.TinyJpeg(2), [new PresenceQuery(1, 1, 1, 1, 1)]));
        await pod.FailAsync(await pod.ClaimAsync(), "the photo cannot be decoded");

        var ex = await Assert.ThrowsAsync<ImageRecognitionUnavailableException>(() => call);
        Assert.Equal(ImageRecognitionUnavailableException.QueueMessage, ex.Message);
        inProcess.DidNotReceiveWithAnyArgs().Score(default!, default!, default!);
    }

    [Fact]
    public async Task NobodyClaimsWithinTheDeadline_InQueueMode_RecognitionIsUnavailable()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var call = new QueuedMarkerDetectionService(inProcessMarkers, FakeRecognitionClaimant.Caller(f))
            .DetectAsync(CaptureScenario.TinyJpeg(), null, CancellationToken.None);
        var job = Assert.Single(await FakeRecognitionClaimant.QueuedAsync(f));
        f.Clock.Advance(f.Options.InteractiveClaimWait + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ImageRecognitionUnavailableException>(() => call);
        Assert.Equal(GpuJobStatus.Cancelled, (await f.RowAsync(job.Id)).Status);
        await inProcessMarkers.DidNotReceiveWithAnyArgs().DetectAsync(default!, default, default);
    }

    [Fact]
    public async Task NobodyClaimsWithinTheDeadline_WithFallback_TheAppRunsIt()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.QueueWithFallback);
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var call = new QueuedMarkerDetectionService(inProcessMarkers, FakeRecognitionClaimant.Caller(f))
            .DetectAsync(CaptureScenario.TinyJpeg(), null, CancellationToken.None);
        await FakeRecognitionClaimant.QueuedAsync(f);
        f.Clock.Advance(f.Options.InteractiveClaimWait + TimeSpan.FromSeconds(1));

        Assert.Same(InApp, await call);
    }

    [Fact]
    public async Task OutsideAWallScope_TheAppRunsIt_AndNothingIsQueued()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);

        var result = await new QueuedMarkerDetectionService(inProcessMarkers, FakeRecognitionClaimant.Caller(f))
            .DetectAsync(CaptureScenario.TinyJpeg(), null, CancellationToken.None);

        Assert.Same(InApp, result);
        await using var db = h.CreateContext();
        Assert.False(await db.RecognitionJobs.AnyAsync());
    }

    [Fact]
    public async Task Detect_ARunnerAnswersWithBoxes_TheAppTurnsThemIntoHolds()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        var inProcess = Substitute.For<IHoldDetectionService>();
        var detector = FakeRecognitionClaimant.Detector(f, inProcess, modelFile);
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var call = detector.DetectHoldsAsync(CaptureScenario.TinyJpeg());
        var job = await pod.ClaimAsync();
        await pod.CompleteAsync(job, FakeRecognitionClaimant.Boxes(1));
        var hold = Assert.Single(await call);

        Assert.Equal(RecognitionKinds.DetectHolds, job.Kind);
        Assert.Equal((0.5, 0.5, 0.05, 0.1), (hold.X, hold.Y, hold.Radius, hold.BoxWidth));
        await inProcess.DidNotReceiveWithAnyArgs().DetectHoldsAsync(default!, default);
    }
}
