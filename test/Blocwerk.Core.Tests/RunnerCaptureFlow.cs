// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A capture whose photo-real view goes to a 3D runner, driven step by step: processed to "Model ready", the job claimed
/// by the wall's own runner, previews and the result uploaded, installs run. For the preview and re-finish tests.
/// </summary>
internal sealed class RunnerCaptureFlow : IDisposable
{
    private static readonly string[] SplitKinds = ["splat", WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind];

    private RunnerCaptureFlow(WallTestHarness harness, CaptureScenario scenario, ICaptureFollowUpStep photoRealStep)
    {
        Harness = harness;
        Scenario = scenario;
        PhotoRealStep = photoRealStep;
    }

    public WallTestHarness Harness { get; }

    public CaptureScenario Scenario { get; }

    /// <summary>The follow-up step that needs the photo-real view (counts its runs).</summary>
    public ICaptureFollowUpStep PhotoRealStep { get; }

    public GpuJobQueue Runners => Scenario.Runners!;

    public MutableTestClock Clock { get; private set; } = null!;

    public GpuRunner Runner { get; private set; } = null!;

    public Guid CaptureId { get; private set; }

    public GpuJob Job { get; private set; } = null!;

    /// <summary>A capture processed to "Model ready" with its GPU job claimed by the wall's runner.</summary>
    public static async Task<RunnerCaptureFlow> StartAsync(WallTestHarness h, GpuRunnerOptions? options = null)
    {
        var clock = new MutableTestClock(DateTimeOffset.UtcNow);
        var step = Step();
        var s = new CaptureScenario(
            h,
            followUps: harness => FollowUpChains.Build(harness.RootContextFactory, step),
            runnerOptions: options ?? new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Always },
            clock: clock);
        var flow = new RunnerCaptureFlow(h, s, step) { Clock = clock };
        s.SplatClient.IsConfigured = true;
        s.SplatClient.Kinds = [.. SplitKinds];
        s.SplatClient.Download = name => name switch
        {
            "bundle.zip" => RunnerFixture.Bundle(),
            "prepared.json" => "{\"version\":1}"u8.ToArray(),
            "frame.json" => System.Text.Encoding.UTF8.GetBytes(s.SplatClient.FrameJson),
            "wall.spz" => s.SplatClient.Spz,
            _ => CaptureScenario.TinyJpeg(),
        };
        flow.CaptureId = await s.StartCaptureAsync();
        flow.Runner = await AddRunnerAsync(h, clock);
        await s.Processor.ProcessAsync(flow.CaptureId, CancellationToken.None);
        flow.Job = (await flow.Runners.TryClaimAsync(flow.Runner, null, CancellationToken.None))!;
        return flow;
    }

    public Task<RunnerJobOutcome> PreviewAsync(int step, int total = 50000) =>
        Runners.AcceptPreviewAsync(Runner, Job.Id, step, total, Upload(), "gzip", CancellationToken.None);

    public Task<RunnerJobOutcome> ResultAsync() =>
        Runners.AcceptResultAsync(Runner, Job.Id, Upload(), "gzip", null, CancellationToken.None);

    public Task InstallPreviewAsync() => Scenario.Processor.InstallPreviewAsync(Job.Id, CancellationToken.None);

    /// <summary>Runs the capture pipeline for the capture (a hand-back or a re-finish enqueued it).</summary>
    public async Task ProcessAsync()
    {
        Assert.Equal(CaptureId, await Scenario.Queue.DequeueAsync(CancellationToken.None));
        await Scenario.Processor.ProcessAsync(CaptureId, CancellationToken.None);
    }

    public int Finishes() => Scenario.SplatClient.MultipartSubmissions.Count(m => m.Kind == WallCaptureProcessor.FinishKind);

    public void Dispose() => Scenario.Dispose();

    private static MemoryStream Upload() => new(RunnerFixture.Gzip(RunnerFixture.SlimPly()));

    private static ICaptureFollowUpStep Step()
    {
        var step = Substitute.For<ICaptureFollowUpStep>();
        step.Key.Returns("photo-real-probe");
        step.Title.Returns("Measuring in the photo-real view");
        step.NeedsPhotoReal.Returns(true);
        step.RunAsync(Arg.Any<CaptureFollowUpContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaptureFollowUpStepResult(CaptureFollowUpOutcome.Done, "measured"));
        return step;
    }

    private static async Task<GpuRunner> AddRunnerAsync(WallTestHarness h, MutableTestClock clock)
    {
        var (token, prefix) = GpuRunnerTokens.Create();
        var runner = new GpuRunner
        {
            Name = "gpu", OwnerUserId = h.Owner.Id, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix,
            LastSeenAt = clock.GetUtcNow(), MaxQuality = "ultra", Trainer = "gsplat", VramMb = 16376,
        };
        await using var db = h.CreateContext();
        db.GpuRunners.Add(runner);
        db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = h.WallId });
        await db.SaveChangesAsync();
        return runner;
    }
}
