// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection.Gating;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Recognition.Remote;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// How the app waits for the queue: a runner's job is awaited without a gate slot, the caller's token cancels an interactive
/// job, a background job in strict <c>queue</c> mode stays queued however long nobody claims it, and a call without a wall
/// in scope is logged once per kind.
/// </summary>
public sealed class RecognitionQueueModeTests : IDisposable
{
    private static readonly PresenceQuery[] Query = [new PresenceQuery(1, 1, 1, 1, 1)];
    private readonly string modelFile = Path.Combine(Path.GetTempPath(), $"holds-model-{Guid.NewGuid():N}.onnx");

    public RecognitionQueueModeTests() => File.WriteAllBytes(modelFile, [1, 2, 3, 4]);

    public void Dispose() => File.Delete(modelFile);

    [Fact]
    public async Task ARemoteWait_HoldsNoGateSlot_ASecondCallerRunsMeanwhile()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        using var gate = new ImageRecognitionGate(1, TimeSpan.FromSeconds(2), NullLogger.Instance);
        var inProcess = Substitute.For<IHoldPresenceProbe>();
        IHoldPresenceProbe probe = new QueuedHoldPresenceProbe(inProcess, FakeRecognitionClaimant.Caller(f), gate);
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var first = probe.ScoreAsync(gate, "look-check", CaptureScenario.TinyJpeg(1), CaptureScenario.TinyJpeg(2), Query);
        var job = await pod.ClaimAsync();
        var second = await gate.RecogniseAsync("other-user", () => 42);
        Assert.False(first.IsCompleted);
        await pod.CompleteAsync(job, "{\"version\":1,\"scores\":[0.5]}");

        Assert.Equal(42, second);
        Assert.Equal(0.5, Assert.Single(await first));
        inProcess.DidNotReceiveWithAnyArgs().Score(default!, default!, default!);
    }

    [Fact]
    public async Task TheCallersToken_CancelsTheInteractiveJob()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var probe = new QueuedHoldPresenceProbe(Substitute.For<IHoldPresenceProbe>(), FakeRecognitionClaimant.Caller(f));
        using var wall = RecognitionWallScope.Begin(h.WallId);
        using var circuit = new CancellationTokenSource();

        var call = probe.ScoreAsync("look-check", CaptureScenario.TinyJpeg(1), CaptureScenario.TinyJpeg(2), Query, circuit.Token);
        var job = Assert.Single(await FakeRecognitionClaimant.QueuedAsync(f));
        await circuit.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(GpuJobStatus.Cancelled, (await f.RowAsync(job.Id)).Status);
    }

    [Fact]
    public async Task ACallWithoutAWall_IsLoggedOncePerKind()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var log = new CapturingLogger<RecognitionCaller>();
        var probe = new QueuedHoldPresenceProbe(Substitute.For<IHoldPresenceProbe>(), FakeRecognitionClaimant.Caller(f, log));

        await probe.ScoreAsync("look-check", CaptureScenario.TinyJpeg(1), CaptureScenario.TinyJpeg(2), Query, CancellationToken.None);
        await probe.ScoreAsync("look-check", CaptureScenario.TinyJpeg(1), CaptureScenario.TinyJpeg(2), Query, CancellationToken.None);

        var line = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains(RecognitionKinds.Presence, line.Message);
    }

    [Fact]
    public async Task InStrictQueueMode_ABackgroundJobNobodyClaims_StaysQueued()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var context = await RunnerHoldDetectionTests.SeedAsync(f.Runners);
        var detection = Detection(f);
        using var stop = new CancellationTokenSource();

        var search = detection.DetectAsync(context, stop.Token);
        var job = Assert.Single(await FakeRecognitionClaimant.QueuedAsync(f));
        f.Clock.Advance(f.Runners.Queue.Options.HoldsMaxWait + TimeSpan.FromMinutes(1));
        await Task.Delay(500);

        Assert.False(search.IsCompleted);
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(job.Id)).Status);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(job.Id)).Status);
    }

    private QueuedCaptureHoldDetection Detection(RecognitionFixture f)
    {
        var settings = new BlocwerkSettings();
        settings.HoldDetection.ModelPath = modelFile;
        var models = new RunnerHoldDetection(
            f.Runners.Queue, f.Runners.Files, f.Harness.RootContextFactory, settings, NullLogger<RunnerHoldDetection>.Instance, f.Clock);
        return new QueuedCaptureHoldDetection(
            f.Queue, models, f.Runners.Queue, f.Runners.Files, settings, NullLogger<QueuedCaptureHoldDetection>.Instance, f.Clock)
        {
            Slice = TimeSpan.FromMilliseconds(50),
        };
    }
}
