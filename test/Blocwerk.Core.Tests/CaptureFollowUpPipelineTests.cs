// <copyright file="CaptureFollowUpPipelineTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The chain inside a real capture run: dropping photos places the existing holds and refines their shapes with
/// no extra step, the capture history says so in plain words, a failing step never fails the capture, and without a
/// reachable GPU worker the capture simply ends "done" (no error, no photo-real view, a quiet note at most).
/// </summary>
public class CaptureFollowUpPipelineTests
{
    private readonly IHoldTexturePlacementService placement = Substitute.For<IHoldTexturePlacementService>();
    private readonly IHoldFootprintService footprints = Substitute.For<IHoldFootprintService>();
    private readonly IHoldProtrusionService protrusion = Substitute.For<IHoldProtrusionService>();
    private readonly IWallVolumeService volumes = Substitute.For<IWallVolumeService>();

    public CaptureFollowUpPipelineTests()
    {
        placement.PlaceFromPipelineAsync(default, default, default, default)
            .ReturnsForAnyArgs(new HoldPlacementResult(Guid.NewGuid(), 856, 3, 19, []));
        footprints.RefineFromPipelineAsync(default, default).ReturnsForAnyArgs(new HoldFootprintRunResult(653, 0, 4, 24));

        // Without a photo-real view or sparse points the services find nothing to measure in (the real ones return null).
        protrusion.MeasureFromPipelineAsync(default, default).ReturnsForAnyArgs((HoldProtrusionRunResult?)null);
        volumes.DetectFromPipelineAsync(default, default).ReturnsForAnyArgs((WallVolumeRunResult?)null);
    }

    [Fact]
    public async Task ACapture_PlacesAndRefinesTheExistingHolds_AndTheHistorySaysSo()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCapturesAsync(h.WallId)).Single();
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.Error);
        Assert.Equal("856 holds placed on the 3D model, 653 hold shapes refined from several photos.", summary.FollowUp);
        Assert.Null(summary.FollowUpNote);
        await placement.Received(1).PlaceFromPipelineAsync(h.WallId, summary.GeometryModelId!.Value, h.Owner.Id, Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            placement.PlaceFromPipelineAsync(h.WallId, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
            footprints.RefineFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AFailingStep_NeverFailsTheCapture_AndTheOthersStillRun()
    {
        using var h = new WallTestHarness();
        placement.PlaceFromPipelineAsync(default, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("matcher crashed"));
        using var s = Scenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCapturesAsync(h.WallId)).Single();
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Equal("653 hold shapes refined from several photos.", summary.FollowUp);
        Assert.Equal("Placing the existing holds on the 3D model failed.", summary.FollowUpNote);
        await footprints.Received(1).RefineFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoGpuWorkerConfigured_TheCaptureIsSimplyDone()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal("Done", capture.Stage);
        Assert.Null(capture.Error);
        Assert.False(s.Service.IsSplatConfigured);
        Assert.Empty(s.SplatClient.MultipartSubmissions);
        var protrusionEntry = CaptureFollowUpRecord.Parse(capture.FollowUpJson).Find("measure-protrusion");
        Assert.Equal(CaptureFollowUpOutcome.Skipped, protrusionEntry!.Outcome);
        var volumeEntry = CaptureFollowUpRecord.Parse(capture.FollowUpJson).Find("detect-volumes");
        Assert.Equal(CaptureFollowUpOutcome.Skipped, volumeEntry!.Outcome);
        await s.Push.DidNotReceive().NotifyWallPhotoRealReadyAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Theory]
    [InlineData(ComputeFailureKind.Unavailable)]
    [InlineData(ComputeFailureKind.Busy)]
    public async Task AnUnreachableGpuWorker_EndsTheCaptureAsDone_WithAQuietNote(ComputeFailureKind kind)
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h);
        s.SplatClient.IsConfigured = true;
        s.SplatClient.SubmitError = new ComputeJobException(kind, "The splat service could not be reached.");
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCapturesAsync(h.WallId)).Single();
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Equal("Done", summary.Stage);
        Assert.Null(summary.Error);
        Assert.False(summary.IsRunning);
        Assert.Equal(WallCaptureProcessor.NoWorkerNote, summary.FollowUpNote);
        Assert.Equal("856 holds placed on the 3D model, 653 hold shapes refined from several photos.", summary.FollowUp);
        await using var db = h.CreateContext();
        Assert.Empty(await db.WallGeometrySplats.ToListAsync());
        Assert.True((await db.WallGeometryModels.SingleAsync()).IsActive);
        await s.Push.Received(1).NotifyWallModelReadyAsync(h.WallId, h.Owner.Id);
        await s.Push.DidNotReceive().NotifyWallPhotoRealReadyAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task WithAPhotoRealView_VolumesAreFound_ThenTheHoldsAreMeasured()
    {
        using var h = new WallTestHarness();
        Scene(sparse: false);
        using var s = Scenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCapturesAsync(h.WallId)).Single();
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Equal(
            "856 holds placed on the 3D model, 653 hold shapes refined from several photos, 6 volumes found, 82 holds placed on them, 12 holds measured in the photo-real view.",
            summary.FollowUp);
        Received.InOrder(() =>
        {
            placement.PlaceFromPipelineAsync(h.WallId, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
            footprints.RefineFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
            volumes.DetectFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
            protrusion.MeasureFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
        });
        await protrusion.Received(1).MeasureFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutAPhotoRealView_TheSparsePointsGiveVolumesAndProtrusion_AndTheHistorySaysSo()
    {
        using var h = new WallTestHarness();
        Scene(sparse: true);
        using var s = Scenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCapturesAsync(h.WallId)).Single();
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Equal(
            "856 holds placed on the 3D model, 653 hold shapes refined from several photos, 6 volumes found, 82 holds placed on them "
            + "(from the sparse points, coarser), 12 holds measured from the sparse points (coarser).",
            summary.FollowUp);
        Received.InOrder(() =>
        {
            volumes.DetectFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
            protrusion.MeasureFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData(6, 82, "6 volumes found, 82 holds placed on them")]
    [InlineData(1, 1, "1 volume found, 1 hold placed on it")]
    [InlineData(3, 0, "3 volumes found")]
    [InlineData(0, 0, "")]
    public void TheVolumeStep_SaysWhatItFound(int found, int placed, string expected) =>
        Assert.Equal(expected, DetectVolumesFollowUpStep.Describe(found, placed));

    /// <summary>The services find a scene to measure in: the photo-real view, or (without one) the sparse points.</summary>
    private void Scene(bool sparse)
    {
        protrusion.MeasureFromPipelineAsync(default, default).ReturnsForAnyArgs(new HoldProtrusionRunResult(12, 1, 0, 12, sparse));
        volumes.DetectFromPipelineAsync(default, default).ReturnsForAnyArgs(new WallVolumeRunResult(6, 2, 82, 82, sparse));
    }

    private CaptureScenario Scenario(WallTestHarness h) => new(h, followUps: harness => FollowUpChains.Build(
        harness.RootContextFactory,
        new PlaceHoldsFollowUpStep(placement),
        new RefineFootprintsFollowUpStep(footprints),
        new MeasureProtrusionFollowUpStep(protrusion),
        new DetectVolumesFollowUpStep(volumes)));
}
