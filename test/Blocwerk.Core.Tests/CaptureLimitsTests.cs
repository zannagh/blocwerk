// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture limits live in one place: the app's two settings, whose defaults are the workers' (one shared file), size every
/// worker cap and timeout; a capture that will not fit is refused before anything is sent; and the stored photo-real quality
/// is what was trained, not what was asked for.
/// </summary>
public class CaptureLimitsTests
{
    [Fact]
    public void TheAppAndTheWorkersReadTheSameDefaults()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedLimitsFile()));
        var shared = doc.RootElement;
        var options = new WallCapturePipelineOptions();
        Assert.Equal(shared.GetProperty("maxPhotos").GetInt32(), options.MaxPhotos);
        Assert.Equal(shared.GetProperty("maxVideoFrames").GetInt32(), options.MaxVideoFrames);
        Assert.Equal(shared.GetProperty("anchorImages").GetInt32(), CaptureLimits.AnchorImages);
        Assert.Equal(shared.GetProperty("baselineImages").GetInt32(), CaptureLimits.BaselineImages);

        // capture_limits.py: wall-geometry takes photos + frames, the splat worker that plus the anchors.
        Assert.Equal(320, CaptureLimits.GeometryImages(options));
        Assert.Equal(352, CaptureLimits.SplatImages(options));
    }

    [Fact]
    public void TimeoutsGrowWithTheLimits_AndNeverShrink()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), CaptureLimits.Scaled(TimeSpan.FromMinutes(30), 120));
        Assert.Equal(TimeSpan.FromMinutes(127.5), CaptureLimits.Scaled(TimeSpan.FromMinutes(30), 850));
    }

    [Fact]
    public void ACaptureBeyondTheLimits_OrAWorkerBelowThem_IsRefused()
    {
        var options = new WallCapturePipelineOptions();
        Assert.Null(CaptureLimits.Refusal(200, 120, options, 320, 352));
        Assert.Contains("429 photos and 400 video frames", CaptureLimits.Refusal(429, 400, options));
        Assert.Null(CaptureLimits.Refusal(429, 400, new WallCapturePipelineOptions { MaxPhotos = 450, MaxVideoFrames = 400 }));
        Assert.Contains("3D model service takes at most 200", CaptureLimits.Refusal(150, 100, options, geometryCap: 200));
        Assert.Contains("photo-real worker takes at most 300", CaptureLimits.Refusal(150, 120, options, 320, splatCap: 300));
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
