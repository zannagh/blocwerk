// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>SPLATSERVICE__ONRUNNERS</c> end to end with a fake runner: the server runs no splat worker. A marker capture is prepared,
/// trained and finished on runners while the capture worker never waits (Model ready at once, the finish parked); a re-finish and
/// a package import finish on a runner too.
/// </summary>
public class RunnerSplatFlowTests
{
    [Fact]
    public async Task AMarkerCapture_IsPreparedTrainedAndFinishedOnRunners_AndNothingRunsOnTheServer()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out var clock);
        var captureId = await s.StartCaptureAsync();

        // No runner online at all, mode auto: still queued for one (never the all-in-one worker), and the capture completes.
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        await s.Runners!.SweepAsync(CancellationToken.None);
        Assert.Equal(
            "Photo-real view pending: waiting for a 3D runner to prepare the photos (none online)",
            (await s.Service.GetCaptureAsync(captureId))!.PhotoRealPending);

        var runner = await FakeSplatRunner.AddAsync(h, s.Runners, h.WallId);
        var prepare = await runner.DeliverAsync(GpuJobKind.SplatPrepare);
        await ProcessNextAsync(s, captureId);
        var installed = await SplatJobSupport.JobAsync(h, prepare.Id);
        Assert.NotNull(installed.InstalledAt);
        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(installed.BundlePath)), "the input envelope is dropped once read");
        Assert.Equal(WallCaptureStatus.Succeeded, (await s.Service.GetCaptureAsync(captureId))!.Status);

        await runner.DeliverAsync(GpuJobKind.Splat);
        await ProcessNextAsync(s, captureId);
        await using (var db = h.CreateContext())
        {
            var parked = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.Splatting, parked.Status);
            Assert.Equal(WallCaptureProcessor.WaitingForRunnerStage, parked.Stage);
            Assert.Equal(0, parked.Attempts);
        }

        await runner.DeliverAsync(GpuJobKind.SplatFinish);
        await ProcessNextAsync(s, captureId);

        await using var after = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await after.WallCaptures.SingleAsync()).Status);
        Assert.Single(await after.WallGeometrySplats.ToListAsync());
        Assert.All(await after.GpuJobs.ToListAsync(), j => Assert.NotNull(j.InstalledAt));
        Assert.Empty(s.SplatClient.MultipartSubmissions);
        Assert.Equal([GpuJobKind.SplatPrepare, GpuJobKind.SplatFinish], runner.Taken.Select(t => t.Kind));
        Assert.Contains("geometry", runner.Taken[0].Parts);
        await s.Push.Received(1).NotifyWallPhotoRealReadyAsync(h.WallId, h.Owner.Id);
    }

    [Fact]
    public async Task ARefinish_FinishesTheKeptResultAgainOnARunner()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out _);
        var (captureId, runner) = await FinishedAsync(h, s);

        Assert.Empty(await s.Service.RefinishPhotoRealAsync(captureId));
        await ProcessNextAsync(s, captureId);
        await using (var db = h.CreateContext())
        {
            Assert.Equal(WallCaptureProcessor.WaitingForRunnerStage, (await db.WallCaptures.SingleAsync()).Stage);
        }

        await runner.DeliverAsync(GpuJobKind.SplatFinish);
        await ProcessNextAsync(s, captureId);

        await using var after = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await after.WallCaptures.SingleAsync()).Status);
        Assert.Equal(2, await after.GpuJobs.CountAsync(j => j.Kind == GpuJobKind.SplatFinish && j.InstalledAt != null));
        Assert.Null((await after.GpuJobs.SingleAsync(j => j.Kind == GpuJobKind.Splat)).RefinishStateJson);
    }

    [Fact]
    public async Task APackageImport_IsAccepted_AndItsDeliveredViewIsFinishedOnARunner()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (exported, bytes) = await f.ExportAsync();
        await f.ForgetAsync(exported);
        var manifest = CapturePackageFlow.WithoutResults(exported);
        var scenario = f.Runner.Scenario;
        var queue = new GpuJobQueue(
            h.RootContextFactory, scenario.Files, scenario.Queue, OnRunners(), new GpuJobSignal(), NullLogger<GpuJobQueue>.Instance, clock: f.Runner.Clock);
        var adapter = new RunnerSplatClient(queue, scenario.Files, NullLogger<RunnerSplatClient>.Instance, TimeSpan.Zero);
        var clients = new FakeComputeJobClientFactory(scenario.Client, adapter);
        var packages = new CapturePackageService(
            h.RootContextFactory, h.CurrentUser, scenario.Files, scenario.Queue, clients, NullLogger<CapturePackageService>.Instance,
            queue.Options, f.Disk, refinements: f.Refinements);
        var processor = new WallCaptureProcessor(
            h.RootContextFactory, scenario.Settings, clients, scenario.Files, scenario.Push, NullLoggerFactory.Instance, scenario.Options,
            scenario.Detector, scenario.Video, gpuJobs: queue);

        var report = await packages.BeginImportAsync(manifest, CancellationToken.None);
        Assert.True(report.CanCommit, string.Join(" | ", report.Blockers));
        await f.UploadAllAsync(manifest, bytes);
        Assert.True((await packages.CommitImportAsync(f.CaptureId, CancellationToken.None)).Committed);
        Assert.Equal(f.CaptureId, await scenario.Queue.DequeueAsync(CancellationToken.None));
        await processor.ProcessAsync(f.CaptureId, CancellationToken.None);
        var runner = await FakeSplatRunner.AddAsync(h, queue, h.WallId);
        await runner.DeliverAsync(GpuJobKind.SplatFinish);
        Assert.Equal(f.CaptureId, await scenario.Queue.DequeueAsync(CancellationToken.None));
        await processor.ProcessAsync(f.CaptureId, CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
        Assert.Single(await db.WallGeometrySplats.Where(x => x.GeometryModelId == manifest.Rows.Model.Id).ToListAsync());
        Assert.NotNull((await db.GpuJobs.SingleAsync(j => j.Kind == GpuJobKind.Splat)).InstalledAt);
    }

    internal static GpuRunnerOptions OnRunners(GpuRunnerMode mode = GpuRunnerMode.Auto) =>
        new() { ClaimWait = TimeSpan.Zero, Mode = mode, SplatOnRunners = true };

    internal static CaptureScenario Scenario(WallTestHarness h, out MutableTestClock clock)
    {
        clock = new MutableTestClock(DateTimeOffset.UtcNow);
        return new CaptureScenario(h, runnerOptions: OnRunners(), clock: clock);
    }

    /// <summary>Runs the capture the queue woke (a runner delivered, or a re-finish).</summary>
    internal static async Task ProcessNextAsync(CaptureScenario s, Guid captureId)
    {
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
    }

    /// <summary>A marker capture whose view a runner prepared, trained and finished.</summary>
    private static async Task<(Guid CaptureId, FakeSplatRunner Runner)> FinishedAsync(WallTestHarness h, CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        var runner = await FakeSplatRunner.AddAsync(h, s.Runners!, h.WallId);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        foreach (var kind in new[] { GpuJobKind.SplatPrepare, GpuJobKind.Splat, GpuJobKind.SplatFinish })
        {
            await runner.DeliverAsync(kind);
            await ProcessNextAsync(s, captureId);
        }

        return (captureId, runner);
    }
}
