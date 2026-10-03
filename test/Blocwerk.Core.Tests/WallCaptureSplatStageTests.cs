// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The optional photo-real stage after textures: server config is the opt-in, the capture's photos
/// leave without metadata, the splat is stored per model, and a splat failure never fails the
/// capture (model and textures stay live). Resumes from the recorded job like every other stage.
/// </summary>
public class WallCaptureSplatStageTests
{
    [Fact]
    public async Task NotConfigured_SkipsTheStageSilently()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Null(capture.SplatJobId);
        Assert.Empty(s.SplatClient.MultipartSubmissions);
        Assert.Empty(await db.WallGeometrySplats.ToListAsync());
        await s.Push.DidNotReceive().NotifyWallPhotoRealReadyAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Success_StoresTheSplatForTheModel_AndNotifies()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal("Done", capture.Stage);
        Assert.Null(capture.Error);
        Assert.NotNull(capture.SplatJobId);
        var splat = await db.WallGeometrySplats.SingleAsync();
        Assert.Equal(capture.GeometryModelId, splat.GeometryModelId);
        Assert.Equal(s.SplatClient.Spz.LongLength, splat.SizeBytes);
        Assert.Equal(s.SplatClient.Spz, await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(splat.StoredPath)!));
        Assert.Equal(s.SplatClient.FrameJson, splat.FrameJson);
        Assert.Equal(2, await db.WallGeometryTextures.CountAsync());
        await s.Push.Received(1).NotifyWallModelReadyAsync(h.WallId, h.Owner.Id);
        await s.Push.Received(1).NotifyWallPhotoRealReadyAsync(h.WallId, h.Owner.Id);
    }

    [Fact]
    public async Task Submission_CarriesGeometryOptionsAndMetadataFreePhotosNamedAsTheSolveCameras()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync(photos: 3);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (kind, parts) = Assert.Single(s.SplatClient.MultipartSubmissions);
        Assert.Equal("splat", kind);
        await using var db = h.CreateContext();
        var geometry = await db.WallGeometryModels.Select(m => m.Json).SingleAsync();
        Assert.Equal(geometry, Encoding.UTF8.GetString(parts.Single(p => p.Name == "geometry").Content));
        var options = JsonNode.Parse(parts.Single(p => p.Name == "options").Content)!;
        Assert.True(options["spz"]!.GetValue<bool>());
        Assert.Null(options["maxSteps"]); // the worker's default unless the server configures one

        var photos = parts.Where(p => p.Name == "photos").ToList();
        Assert.Equal(["p01.jpg", "p02.jpg", "p03.jpg"], photos.Select(p => p.FileName));
        Assert.All(photos, p => Assert.Equal("image/jpeg", p.ContentType));
        Assert.All(photos, p => Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(p.Content), StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerFailure_LeavesModelAndTextures_AndRecordsWhy()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.SplatClient.Terminal["splat"] = new ComputeJobStatus
        {
            Status = ComputeJobStates.Failed, Error = "sfm-mapping: only 3/14 images registered (need 7)",
        };
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, capture.Status);
        Assert.Equal("Done (without the photo-real view)", capture.Stage);
        Assert.Contains("photo-real view could not be made", capture.Error);
        Assert.Contains("only 3/14 images registered", capture.Error);
        Assert.True((await db.WallGeometryModels.SingleAsync()).IsActive);
        Assert.Equal(2, await db.WallGeometryTextures.CountAsync());
        Assert.Empty(await db.WallGeometrySplats.ToListAsync());
        await s.Push.Received(1).NotifyWallModelReadyAsync(h.WallId, h.Owner.Id);
        await s.Push.DidNotReceive().NotifyWallPhotoRealReadyAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task UnalignedFrame_OrUnreachableWorker_AreSplatFailuresToo()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.SplatClient.FrameJson = FakeComputeJobClient.SplatFrame(aligned: false);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, capture.Status);
            Assert.Contains("could not be aligned", capture.Error);
        }

        using var h2 = new WallTestHarness();
        using var s2 = new CaptureScenario(h2);
        s2.SplatClient.IsConfigured = true;
        s2.SplatClient.SubmitError = new ComputeJobException(ComputeFailureKind.Unauthorized, "The splat service refused the API key.");
        var second = await s2.StartCaptureAsync();

        await s2.Processor.ProcessAsync(second, CancellationToken.None);

        await using var db2 = h2.CreateContext();
        var failed = await db2.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, failed.Status);
        Assert.Contains("refused the API key", failed.Error);
        Assert.True((await db2.WallGeometryModels.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task TextureFailure_StillRunsTheSplat_AndKeepsBothOutcomes()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.Client.Terminal["textures"] = new ComputeJobStatus { Status = ComputeJobStates.Failed, Error = "no coverage" };
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutTextures, capture.Status);
        Assert.Contains("textures could not be made", capture.Error);
        Assert.NotNull(await db.WallGeometrySplats.SingleOrDefaultAsync());
    }

    [Fact]
    public async Task Restart_WhileSplatting_ResumesTheRecordedJob_WithoutRedoingModelOrTextures()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        s.SplatClient.IsConfigured = true;
        var jobId = s.SplatClient.Adopt("splat");
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            capture.Status = WallCaptureStatus.Splatting;
            capture.SplatJobId = jobId;
            await db.SaveChangesAsync();
        }

        var geometrySubmissions = s.Client.JsonSubmissions.Count + s.Client.MultipartSubmissions.Count;
        await new WallCaptureWorker(
                h.RootContextFactory, s.Queue, s.Processor, s.Files, s.Options, NullLogger<WallCaptureWorker>.Instance)
            .RecoverAsync(CancellationToken.None);
        var dequeued = await s.Queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await s.Processor.ProcessAsync(dequeued, CancellationToken.None);

        await using var read = h.CreateContext();
        var resumed = await read.WallCaptures.SingleAsync();
        Assert.Equal(captureId, dequeued);
        Assert.Equal(WallCaptureStatus.Succeeded, resumed.Status);
        Assert.Equal(jobId, resumed.SplatJobId);
        Assert.Empty(s.SplatClient.MultipartSubmissions); // resumed, not re-submitted
        Assert.Equal(geometrySubmissions, s.Client.JsonSubmissions.Count + s.Client.MultipartSubmissions.Count);
        Assert.Equal(resumed.GeometryModelId, (await read.WallGeometrySplats.SingleAsync()).GeometryModelId);
    }

    [Fact]
    public async Task SplatThatKeepsDying_EndsWithoutTheSplat_NotAsAFailedCapture()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        s.SplatClient.IsConfigured = true;
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            capture.Status = WallCaptureStatus.Splatting;
            capture.SplatJobId = s.SplatClient.Adopt("splat");
            capture.Attempts = s.Options.MaxAttempts;
            await db.SaveChangesAsync();
        }

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var read = h.CreateContext();
        var ended = await read.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, ended.Status);
        Assert.Contains("interrupted too often", ended.Error);
        Assert.True((await read.WallGeometryModels.SingleAsync()).IsActive);
    }

    [Fact]
    public void ProgressLabel_NamesTheStageAndItsDetail()
    {
        var status = new ComputeJobStatus { Status = ComputeJobStates.Running, Stage = "train", StageDetail = "step 1200/15000" };

        Assert.Equal("Photo-real view: training (step 1200/15000)", CaptureSplatDocuments.Describe(status));
        Assert.Equal("Photo-real view: matching photos", CaptureSplatDocuments.Describe(status with { Stage = "sfm-matching", StageDetail = null }));
    }

    [Fact]
    public void WorldMatrix_IsTheColumnMajorOfTheRowMajorToWorldMm()
    {
        var m = CaptureSplatDocuments.WorldMatrix(FakeComputeJobClient.SplatFrame(aligned: true))!;

        Assert.Equal(new double[] { 1000, 0, 0, 0, 0, 1000, 0, 0, 0, 0, 1000, 0, 10, 20, 30, 1 }, m);
        Assert.Null(CaptureSplatDocuments.WorldMatrix("{\"toWorldMm\": [[1, 2]]}"));
    }

    [Fact]
    public void WorldMatrix_PrefersAnAppliedRefinement()
    {
        const string Frame = """
            {"toWorldMm": [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]],
             "refinement": {"applied": true, "before": {"medianAbsMm": 8.3}, "after": {"medianAbsMm": 5.9},
                            "toWorldMm": [[1, 0, 0, 5], [0, 1, 0, 6], [0, 0, 1, 7], [0, 0, 0, 1]]}}
            """;

        Assert.Equal(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 6, 7, 1 }, CaptureSplatDocuments.WorldMatrix(Frame));
        Assert.Equal(0, CaptureSplatDocuments.WorldMatrix(Frame.Replace("true", "false", StringComparison.Ordinal))![12]);
        Assert.Equal("n/a; wall plane 8.3 → 5.9 mm", CaptureSplatDocuments.ResidualText(Frame));
    }
}
