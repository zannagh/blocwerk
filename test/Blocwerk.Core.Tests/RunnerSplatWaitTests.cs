// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Waiting for a 3D runner with <c>SPLATSERVICE__ONRUNNERS</c>: a markerless reconstruction parks the capture (a restart parks it
/// again, a delivery resumes it), a day without a runner sends one push, and turning the flag off cancels the runner jobs and the
/// capture carries on with the splat worker.
/// </summary>
public class RunnerSplatWaitTests
{
    [Fact]
    public async Task AMarkerlessCapture_ParksUntilARunnerReconstructs_SurvivesARestart_AndResumes()
    {
        using var h = new WallTestHarness();
        var clock = new MutableTestClock(DateTimeOffset.UtcNow);
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] }, runners: RunnerSplatFlowTests.OnRunners(), clock: clock);
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false, gravityKnown: false);
        var captureId = await MarkerlessFixture.StartAsync(s);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await AssertParkedAsync(h);

        // A restart (the worker re-runs every running capture) waits for the same job instead of submitting again.
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await AssertParkedAsync(h);

        var runner = await FakeSplatRunner.AddAsync(h, s.Runners!, h.WallId);
        runner.Sparse = SparseFixture.Zip(SparseFixture.Surface((_, _) => 0, stepMm: 250));
        var reconstruction = await runner.DeliverAsync(GpuJobKind.SplatPrepare);
        await RunnerSplatFlowTests.ProcessNextAsync(s, captureId);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        var model = await db.WallGeometryModels.SingleAsync();
        Assert.Equal(WallGeometryFrameSource.Features, model.FrameSource);
        Assert.Equal(RunnerSplatClient.IdPrefix + reconstruction.Id, capture.SfmJobId);
        Assert.NotNull((await SplatJobSupport.JobAsync(h, reconstruction.Id)).InstalledAt);

        // The view trains from the reconstruction's own bundle: no second prepare, and its prepared state carries the model.
        var training = await db.GpuJobs.SingleAsync(j => j.Kind == GpuJobKind.Splat);
        Assert.Equal(model.Id, training.GeometryModelId);
        var prepared = JsonNode.Parse(await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(training.PreparedPath)!))!;
        Assert.Equal("features", prepared["geometry"]?["world"]?["frameSource"]?.GetValue<string>());
        Assert.Equal(1, await db.GpuJobs.CountAsync(j => j.Kind == GpuJobKind.SplatPrepare));
    }

    [Fact]
    public async Task ADayWithoutARunner_SaysSo_AndPushesTheCreatorOnce()
    {
        using var h = new WallTestHarness();
        using var s = RunnerSplatFlowTests.Scenario(h, out var clock);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(23));
        await s.Runners!.SweepAsync(CancellationToken.None);
        await s.Push.DidNotReceive().NotifyNoRunnerAsync(Arg.Any<Guid>(), Arg.Any<Guid>());

        clock.Advance(TimeSpan.FromHours(2));
        await s.Runners.SweepAsync(CancellationToken.None);
        await s.Runners.SweepAsync(CancellationToken.None);

        await s.Push.Received(1).NotifyNoRunnerAsync(h.WallId, h.Owner.Id);
        Assert.Equal(
            "Photo-real view pending: waiting for a 3D runner to prepare the photos (none online for a day)",
            (await s.Service.GetCaptureAsync(captureId))!.PhotoRealPending);
    }

    [Fact]
    public async Task TurningTheFlagOff_CancelsTheRunnerJob_AndTheCaptureCarriesOnWithTheSplatWorker()
    {
        using var h = new WallTestHarness();
        using var s = RunnerSplatFlowTests.Scenario(h, out var clock);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        GpuJob waiting;
        await using (var db = h.CreateContext())
        {
            waiting = await db.GpuJobs.SingleAsync();
        }

        // The app restarts with the flag off and the splat worker back (the rollback).
        var off = new GpuJobQueue(
            h.RootContextFactory, s.Files, s.Queue, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero }, new GpuJobSignal(),
            NullLogger<GpuJobQueue>.Instance, clock: clock);
        var worker = new FakeComputeJobClient
        {
            Service = Compute.ComputeServiceKind.Splat, Kinds = ["splat", WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind],
            Download = name => name == WallCaptureProcessor.BundleFile ? RunnerFixture.Bundle() : "{\"version\":1}"u8.ToArray(),
        };
        var processor = new WallCaptureProcessor(
            h.RootContextFactory, s.Settings, new FakeComputeJobClientFactory(s.Client, worker), s.Files, s.Push, NullLoggerFactory.Instance,
            s.Options, s.Detector, s.Video, gpuJobs: off);
        await off.SweepAsync(CancellationToken.None);
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await processor.ProcessAsync(captureId, CancellationToken.None);

        var cancelled = await SplatJobSupport.JobAsync(h, waiting.Id);
        Assert.Equal(GpuJobStatus.Cancelled, cancelled.Status);
        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(waiting.BundlePath)));
        Assert.Equal([WallCaptureProcessor.PrepareKind], worker.MultipartSubmissions.Select(m => m.Kind));
        await using var after = h.CreateContext();
        Assert.Equal(GpuJobStatus.Queued, (await after.GpuJobs.SingleAsync(j => j.Kind == GpuJobKind.Splat)).Status);
        Assert.Equal(WallCaptureStatus.Succeeded, (await after.WallCaptures.SingleAsync()).Status);
    }

    private static async Task AssertParkedAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Solving, capture.Status);
        Assert.Equal(WallCaptureProcessor.ReconstructWaitStage, capture.Stage);
        Assert.Equal(0, capture.Attempts);
        Assert.Null(capture.GeometryModelId);
        var job = await db.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobKind.SplatPrepare, GpuJobStatus.Queued), (job.Kind, job.Status));
    }
}
