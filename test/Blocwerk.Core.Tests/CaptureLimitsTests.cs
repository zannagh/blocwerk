// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture limits live in one place: the app's two settings (same defaults and ranges as the workers, one shared file)
/// size every worker cap and timeout; a capture that will not fit is refused before anything is sent; and the stored
/// photo-real quality is what was trained, not what was asked for.
/// </summary>
public class CaptureLimitsTests
{
    private static readonly CaptureFit[] RedoPaths = [CaptureFit.Start, CaptureFit.Retrain, CaptureFit.Resolve, CaptureFit.Rerender];

    [Fact]
    public void TheAppAndTheWorkersReadTheSameNumbers()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedLimitsFile()));
        var shared = doc.RootElement;
        var options = new WallCapturePipelineOptions();
        Assert.Equal(shared.GetProperty("maxPhotos").GetInt32(), options.MaxPhotos);
        Assert.Equal(shared.GetProperty("maxVideoFrames").GetInt32(), options.MaxVideoFrames);
        Assert.Equal(Range(shared, "photosRange"), (CaptureLimits.MinPhotos, CaptureLimits.MaxPhotosCeiling));
        Assert.Equal(Range(shared, "videoFramesRange"), (CaptureLimits.MinVideoFrames, CaptureLimits.MaxVideoFramesCeiling));
        Assert.Equal(shared.GetProperty("anchorImages").GetInt32(), CaptureLimits.AnchorImages);
        Assert.Equal(AnchorPhotoSelector.DefaultCount, CaptureLimits.AnchorImages);
        Assert.Equal(shared.GetProperty("baselineImages").GetInt32(), CaptureLimits.BaselineImages);
        Assert.Equal(shared.GetProperty("splatFloorImages").GetInt32(), CaptureLimits.SplatFloorImages);

        // capture_limits.py: wall-geometry takes the photos, the splat worker photos + frames + anchors (at least 600).
        Assert.Equal(200, CaptureLimits.GeometryImages(options));
        Assert.Equal(600, CaptureLimits.SplatImages(options));
    }

    [Theory]
    [InlineData("1", "401", 200, 120)] // outside the ranges: the defaults, as capture_limits.py does
    [InlineData("1000", "3", 1000, 3)]
    public void ValuesOutsideTheRanges_KeepTheDefaults_LikeTheWorkers(string photos, string frames, int expectedPhotos, int expectedFrames)
    {
        var options = Bind(photos, frames);
        Assert.Equal((expectedPhotos, expectedFrames), (options.MaxPhotos, options.MaxVideoFrames));
    }

    [Fact]
    public void TimeoutsGrowWithTheLimits_AndNeverShrink()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), CaptureLimits.Scaled(TimeSpan.FromMinutes(30), 120));
        Assert.Equal(TimeSpan.FromMinutes(127.5), CaptureLimits.Scaled(TimeSpan.FromMinutes(30), 850));
        var defaults = new WallCapturePipelineOptions();
        Assert.Equal(TimeSpan.FromMinutes(30), CaptureLimits.ScaledFor(Compute.ComputeServiceKind.Geometry, TimeSpan.FromMinutes(30), defaults));
        Assert.Equal(TimeSpan.FromHours(4), CaptureLimits.ScaledFor(Compute.ComputeServiceKind.Splat, TimeSpan.FromHours(4), defaults));
    }

    [Fact]
    public void AnUnextractedVideo_CountsTheFramesItsLengthYields()
    {
        var options = new WallCapturePipelineOptions();
        Assert.Equal(25, CaptureLimits.ExpectedFrames(10, options)); // 10 s at 2.5 frames a second
        Assert.Equal(120, CaptureLimits.ExpectedFrames(600, options)); // never more than the limit
        Assert.Equal(0, CaptureLimits.ExpectedFrames(null, options));
    }

    [Fact]
    public void TheVideoFramesNeverCountAgainstTheGeometryWorker()
    {
        var options = new WallCapturePipelineOptions();
        Assert.Null(CaptureLimits.Refusal(150, 120, options, CaptureFit.Resolve, geometryCap: 150));
        Assert.Contains("3D model service takes at most 150", CaptureLimits.Refusal(151, 0, options, CaptureFit.Resolve, geometryCap: 150));
        Assert.Contains("sends 285 images; the photo-real worker takes at most 284", CaptureLimits.Refusal(150, 120, options, CaptureFit.Retrain, splatCap: 284));
    }

    [Fact]
    public void TheAtticCapture_PassesEveryPath_OnceOnlyTheCaptureLimitsAreRaised()
    {
        var options = Bind("450", "400");
        var (geometry, splat) = WorkerCaps(options, geometryOverride: null, splatOverride: null);
        Assert.Equal((450, 865), (geometry, splat));
        foreach (var path in RedoPaths)
        {
            Assert.Null(CaptureLimits.Refusal(429, 400, options, path, geometry, splat));
        }
    }

    [Fact]
    public void TheAtticCapture_WithTodaysProdEnvironment_IsRefusedOnEveryPath_AndSaysWhy()
    {
        // CAPTURE__MAXPHOTOS=400, CAPTURE__MAXVIDEOFRAMES=200, GEOMETRY_MAX_PHOTOS=400, SPLAT_MAX_PHOTOS=640.
        var prod = Bind("400", "200");
        var (geometry, splat) = WorkerCaps(prod, geometryOverride: 400, splatOverride: 640);
        foreach (var path in RedoPaths)
        {
            Assert.Contains("429 photos and 400 video frames; this server takes at most 400 photos and 200 frames", CaptureLimits.Refusal(429, 400, prod, path, geometry, splat));
        }

        // With only the app's limits raised, the worker overrides still refuse: the model service for 429 photos > 400,
        // the photo-real worker for 429 + 400 + 15 = 844 images > 640.
        var raised = Bind("450", "400");
        Assert.Contains("3D model service takes at most 400", CaptureLimits.Refusal(429, 400, raised, CaptureFit.Start, geometry, splat));
        Assert.Contains("photo-real worker takes at most 640", CaptureLimits.Refusal(429, 400, raised, CaptureFit.Retrain, geometry, splat));
        Assert.Contains("3D model service takes at most 400", CaptureLimits.Refusal(429, 400, raised, CaptureFit.Resolve, geometry, splat));
        Assert.Contains("3D model service takes at most 400", CaptureLimits.Refusal(429, 400, raised, CaptureFit.Rerender, geometry, splat));
    }

    [Fact]
    public async Task ACaptureAWorkerWouldRefuse_IsRefusedAtStart_BeforeAnythingIsSent()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.MaxImages = 1;
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        for (var i = 0; i < 2; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: i)), CancellationToken.None);
        }

        var problems = await s.Service.StartAsync(draft.CaptureId, await s.Service.SuggestDeclarationsAsync(draft.CaptureId), null);

        Assert.Contains(problems, p => p.Contains("takes at most 1 per job", StringComparison.Ordinal));
        Assert.Empty(s.Client.JsonSubmissions);
        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Draft, (await db.WallCaptures.SingleAsync()).Status);
    }

    [Fact]
    public void TheTrainedQuality_IsReadFromTheFrame()
    {
        var frame = JsonNode.Parse(FakeComputeJobClient.SplatFrame(aligned: true))!.AsObject();
        Assert.Null(CaptureSplatDocuments.TrainedQuality(frame.ToJsonString()));
        frame["stats"] = new JsonObject { ["quality"] = "ultra", ["qualityRequested"] = "high" };
        Assert.Equal(SplatQuality.Ultra, CaptureSplatDocuments.TrainedQuality(frame.ToJsonString()));
        Assert.Null(CaptureSplatDocuments.TrainedQuality("not json"));
    }

    [Fact]
    public async Task AViewTrainedAtAnotherQuality_IsStoredAsWhatItIs()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var frame = JsonNode.Parse(FakeComputeJobClient.SplatFrame(aligned: true))!.AsObject();
        frame["stats"] = new JsonObject { ["quality"] = "ultra" };
        s.SplatClient.FrameJson = frame.ToJsonString();
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(SplatQuality.Ultra, capture.SplatQuality); // started at High, trained at Ultra by the worker's override
    }

    /// <summary>The workers' caps as capture_limits.py derives them; an explicit MAX_PHOTOS wins.</summary>
    private static (int Geometry, int Splat) WorkerCaps(WallCapturePipelineOptions options, int? geometryOverride, int? splatOverride) =>
        (geometryOverride ?? CaptureLimits.GeometryImages(options), splatOverride ?? CaptureLimits.SplatImages(options));

    private static WallCapturePipelineOptions Bind(string photos, string frames) =>
        WallCapturePipelineOptions.Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blocwerk:Capture:MaxPhotos"] = photos,
            ["Blocwerk:Capture:MaxVideoFrames"] = frames,
        }).Build());

    private static (int, int) Range(JsonElement shared, string name) =>
        (shared.GetProperty(name)[0].GetInt32(), shared.GetProperty(name)[1].GetInt32());

    private static string SharedLimitsFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "docker", "compute-jobs-py", "computejobs", "capture_limits.json");
            if (File.Exists(file))
            {
                return file;
            }
        }

        throw new FileNotFoundException("docker/compute-jobs-py/computejobs/capture_limits.json not found above the test binaries");
    }
}
