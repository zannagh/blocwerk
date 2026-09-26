// <copyright file="MarkerlessCaptureTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Captures of walls without (usable) markers: the mode is chosen after detection; mode Features reconstructs the photos
/// once on the splat worker (splat-prepare), solves them with solve-sfm, and later trains the photo-real view from that
/// same bundle on a 3D runner. Marker walls keep the marker solve; without the SfM services nothing changes.
/// </summary>
public class MarkerlessCaptureTests
{
    [Fact]
    public async Task FirstCaptureWithoutMarkers_IsMeasuredFromFeatures_AndTrainsFromThePreparedBundle()
    {
        using var h = new WallTestHarness();
        var clock = new MutableTestClock(DateTimeOffset.UtcNow);
        var runners = new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Always };
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] }, runners: runners, clock: clock);
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false, gravityKnown: false);
        var captureId = await MarkerlessFixture.StartAsync(s);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(WallCaptureGeometryMode.Features, capture.GeometryMode);
        Assert.Null(capture.AnchorCaptureId);
        Assert.Equal(WallCaptureProcessor.PrepareMark + capture.SfmJobId, capture.SplatJobId);
        var model = await db.WallGeometryModels.SingleAsync();
        Assert.True(model.IsActive);
        Assert.Equal(WallGeometryFrameSource.Features, model.FrameSource);

        // One reconstruction (no geometry, no anchors), no marker solve, one feature solve on its sparse.zip.
        var prepare = Assert.Single(s.SplatClient.MultipartSubmissions);
        Assert.Equal(WallCaptureProcessor.PrepareKind, prepare.Kind);
        Assert.DoesNotContain(prepare.Parts, p => p.Name == "geometry");
        Assert.Equal(["p01.jpg", "p02.jpg", "p03.jpg"], prepare.Parts.Where(p => p.Name == "photos").Select(p => p.FileName));
        Assert.Empty(s.Client.JsonSubmissions);
        var solve = s.Client.MultipartSubmissions.First();
        Assert.Equal(MarkerlessCaptureSupport.SolveKind, solve.Kind);
        Assert.Contains(solve.Parts, p => p.Name == "sparse" && p.FileName == "sparse.zip");
        Assert.Null(JsonNode.Parse(solve.Parts.Single(p => p.Name == "request").Content)!["anchors"]);

        // The photo-real view waits for a runner with the SAME bundle; its prepared.json now carries the model.
        var job = await db.GpuJobs.SingleAsync();
        Assert.Equal(model.Id, job.GeometryModelId);
        var prepared = JsonNode.Parse(await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(job.PreparedPath)!))!;
        Assert.Equal("features", prepared["geometry"]?["world"]?["frameSource"]?.GetValue<string>());
    }

    [Fact]
    public async Task MarkerWall_KeepsTheMarkerSolve_EvenWhenFeaturesAreAvailable()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector());
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(WallCaptureGeometryMode.Markers, capture.GeometryMode);
        Assert.Null(capture.SfmJobId);
        Assert.Equal(["solve"], s.Client.JsonSubmissions.Select(j => j.Kind));
        Assert.DoesNotContain(s.Client.MultipartSubmissions, m => m.Kind == MarkerlessCaptureSupport.SolveKind);
        Assert.Equal(["splat"], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        Assert.Equal(WallGeometryFrameSource.Markers, (await db.WallGeometryModels.SingleAsync()).FrameSource);
    }

    [Fact]
    public async Task WithoutTheSfmServices_CapturesWithoutMarkersAreRefusedAsBefore()
    {
        using var h = new WallTestHarness();
        var detector = new SwitchableMarkerDetector { Ids = [] };
        using var s = MarkerlessFixture.Scenario(h, detector, sfm: false);
        await h.SeedWallAsync(holdCount: 0);

        var refused = await Assert.ThrowsAsync<UserFacingException>(() => s.Service.CreateDraftAsync(h.WallId));
        Assert.Contains("Switch on printed markers", refused.Message);

        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        for (var i = 0; i < 2; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: i)), CancellationToken.None);
        }

        var problems = await s.Service.StartAsync(draft.CaptureId, new CaptureDeclarations([], []), null);
        Assert.Contains("At least two photos must show markers.", problems);
    }

    [Fact]
    public async Task ServicesGoneBeforeProcessing_TheMarkerSolveRefusesAsBefore()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        var captureId = await MarkerlessFixture.StartAsync(s);
        s.Client.Kinds = ["solve", "textures"];

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Equal(WallCaptureGeometryMode.Markers, capture.GeometryMode);
        Assert.StartsWith("Only 0 photo(s) show a marker", capture.Error);
        Assert.Empty(s.SplatClient.MultipartSubmissions);
    }

    [Fact]
    public async Task WithoutARunner_TheCaptureEndsQuietlyWithoutThePhotoRealView()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var captureId = await MarkerlessFixture.StartAsync(s);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal(WallCaptureStatus.Succeeded, summary.Status);
        Assert.Null(summary.Error);
        Assert.Equal(WallCaptureProcessor.NoRunnerNote, summary.FollowUpNote);
        Assert.Equal([WallCaptureProcessor.PrepareKind], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        await using var db = h.CreateContext();
        Assert.Empty(await db.WallGeometrySplats.ToListAsync());
        Assert.Empty(await db.GpuJobs.ToListAsync());
    }

    [Fact]
    public async Task ResumedAfterTheReconstructionWasSent_PollsIt_AndSolvesOnce()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var captureId = await MarkerlessFixture.StartAsync(s);
        var sfmJob = s.SplatClient.Adopt(WallCaptureProcessor.PrepareKind);
        await CrashAsync(h, captureId, c => (c.GeometryMode, c.SfmJobId) = (WallCaptureGeometryMode.Features, sfmJob));

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(sfmJob, capture.SfmJobId);
        Assert.Empty(s.SplatClient.MultipartSubmissions);
        Assert.Single(s.Client.MultipartSubmissions, m => m.Kind == MarkerlessCaptureSupport.SolveKind);
    }

    [Fact]
    public async Task ResumedAfterTheSolveWasSent_PollsIt_AndReconstructsNothing()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var captureId = await MarkerlessFixture.StartAsync(s);
        var sfmJob = s.SplatClient.Adopt(WallCaptureProcessor.PrepareKind);
        var solveJob = s.Client.Adopt(MarkerlessCaptureSupport.SolveKind);
        await CrashAsync(h, captureId, c => (c.GeometryMode, c.SfmJobId, c.SolveJobId) = (WallCaptureGeometryMode.Features, sfmJob, solveJob));

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(solveJob, capture.SolveJobId);
        Assert.Empty(s.SplatClient.MultipartSubmissions);
        Assert.DoesNotContain(s.Client.MultipartSubmissions, m => m.Kind == MarkerlessCaptureSupport.SolveKind);
        Assert.True((await db.WallGeometryModels.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task AReconstructionTheWorkerForgot_IsSentAgainOnce()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var captureId = await MarkerlessFixture.StartAsync(s);
        var lost = s.SplatClient.Adopt(WallCaptureProcessor.PrepareKind);
        s.SplatClient.Forgotten.Add(lost);
        await CrashAsync(h, captureId, c => (c.GeometryMode, c.SfmJobId) = (WallCaptureGeometryMode.Features, lost));

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.NotEqual(lost, capture.SfmJobId);
        Assert.Single(s.SplatClient.MultipartSubmissions);
    }

    internal static string RequestOf((string Kind, IReadOnlyList<Compute.ComputeJobPart> Parts) submission) =>
        Encoding.UTF8.GetString(submission.Parts.Single(p => p.Name == "request").Content);

    private static async Task CrashAsync(WallTestHarness h, Guid captureId, Action<WallCapture> change)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        change(capture);
        capture.Status = WallCaptureStatus.Solving;
        await db.SaveChangesAsync();
    }
}
