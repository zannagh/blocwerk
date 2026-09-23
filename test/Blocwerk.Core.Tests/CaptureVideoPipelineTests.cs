// <copyright file="CaptureVideoPipelineTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The walk-along video of a capture: its frames reach ONLY the splat request (as auxiliary
/// <c>vf_</c> images after the photos), never marker detection, the solve, the photo limit or the
/// panel photos; the video is deleted once they are taken.
/// </summary>
public class CaptureVideoPipelineTests
{
    [Fact]
    public async Task Frames_RideAlongInTheSplatRequest_AfterThePhotos()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync(photos: 2, beforeStart: id => AddVideoAsync(s, id));

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (_, parts) = Assert.Single(s.SplatClient.MultipartSubmissions);
        var images = parts.Where(p => p.Name == "photos").Select(p => p.FileName).ToList();
        Assert.Equal(["p01.jpg", "p02.jpg", "vf_0001.jpg", "vf_0002.jpg", "vf_0003.jpg", "vf_0004.jpg", "vf_0005.jpg"], images);
        Assert.All(parts.Where(p => p.FileName?.StartsWith(CaptureSplatDocuments.FramePrefix, StringComparison.Ordinal) == true),
            p => Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(p.Content), StringComparison.Ordinal));
        var (_, request) = Assert.Single(s.Video.Extracted);
        Assert.Equal(s.Options.MaxVideoFrames, request.MaxFrames);
    }

    [Fact]
    public async Task Frames_NeverReachDetectionTheSolveOrThePhotoList_AndTheVideoIsDeleted()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        string? videoFile = null;
        var captureId = await s.StartCaptureAsync(photos: 2, beforeStart: async id =>
        {
            await AddVideoAsync(s, id);
            await using var db = h.CreateContext();
            videoFile = await db.WallCaptures.Where(c => c.Id == id).Select(c => c.VideoStoredPath).SingleAsync();
        });

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var detector = (FakeMarkerDetectionService)s.Detector;
        Assert.Equal(2, detector.Calls.Count); // one per uploaded photo, none for the 5 frames
        var solve = Assert.Single(s.Client.JsonSubmissions, j => j.Kind == "solve");
        Assert.DoesNotContain(CaptureSplatDocuments.FramePrefix, solve.Json, StringComparison.Ordinal);
        Assert.Equal(2, (await s.Service.GetPhotosAsync(captureId)).Count);

        await using var check = h.CreateContext();
        var capture = await check.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(2, await check.WallCapturePhotos.CountAsync());
        Assert.Null(capture.VideoStoredPath);
        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(videoFile!)));
        var frames = CaptureVideoFiles.Frames(capture.VideoFramesJson);
        Assert.Equal(5, frames.Count);
        Assert.All(frames, f => Assert.True(File.Exists(s.Files.ResolvePhysicalPath(f))));
    }

    [Fact]
    public async Task AVideo_DoesNotCountAgainstMaxPhotos()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await AddVideoAsync(s, draft.CaptureId);

        for (var i = 0; i < WallCapturePipelineOptions.MaxPhotos; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: i)), CancellationToken.None);
        }

        var reloaded = await s.Service.GetDraftAsync(h.WallId);
        Assert.Equal(WallCapturePipelineOptions.MaxPhotos, reloaded!.Photos.Count);
        Assert.NotNull(reloaded.Video);
        Assert.Equal(42, reloaded.Video!.DurationSeconds);
    }

    [Fact]
    public async Task UnreadableVideo_CostsOnlyItsFrames()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.Video.Failure = new InvalidDataException("ffmpeg failed (1): moov atom not found");
        var captureId = await s.StartCaptureAsync(photos: 2, beforeStart: id => AddVideoAsync(s, id));

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (_, parts) = Assert.Single(s.SplatClient.MultipartSubmissions);
        Assert.Equal(["p01.jpg", "p02.jpg"], parts.Where(p => p.Name == "photos").Select(p => p.FileName));
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal("[]", capture.VideoFramesJson);
        Assert.Null(capture.VideoStoredPath);
    }

    [Fact]
    public async Task Upload_IsRefused_WithoutASplatWorker_ForANonVideo_AndBeyondTheCap()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, options: new WallCapturePipelineOptions { MaxVideoBytes = 1024 });
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);

        await Assert.ThrowsAsync<Blocwerk.Core.Services.UserFacingException>(
            () => s.Service.AddVideoAsync(draft.CaptureId, "walk.mp4", FakeVideoFrameExtractor.Mp4Stream(512), CancellationToken.None));
        s.SplatClient.IsConfigured = true;
        await Assert.ThrowsAsync<Blocwerk.Core.Services.UserFacingException>(
            () => s.Service.AddVideoAsync(draft.CaptureId, "walk.mp4", FakeVideoFrameExtractor.Mp4Stream(2048), CancellationToken.None));
        await Assert.ThrowsAsync<Blocwerk.Core.Services.UserFacingException>(
            () => s.Service.AddVideoAsync(draft.CaptureId, "walk.jpg", FakeVideoFrameExtractor.Mp4Stream(512), CancellationToken.None));
        await Assert.ThrowsAsync<Blocwerk.Core.Services.UserFacingException>(
            () => s.Service.AddVideoAsync(draft.CaptureId, "walk.mp4", new MemoryStream(new byte[512]), CancellationToken.None));

        Assert.Empty(s.Files.ListFiles()); // a refused upload leaves nothing behind
        Assert.Null((await s.Service.GetDraftAsync(h.WallId))!.Video);
    }

    [Fact]
    public async Task DiscardingTheDraft_DeletesTheVideo()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await AddVideoAsync(s, draft.CaptureId);
        Assert.Single(s.Files.ListFiles());

        await s.Service.DiscardDraftAsync(draft.CaptureId);

        Assert.Empty(s.Files.ListFiles());
    }

    private static async Task AddVideoAsync(CaptureScenario s, Guid captureId)
    {
        await using var video = FakeVideoFrameExtractor.Mp4Stream();
        var info = await s.Service.AddVideoAsync(captureId, "walk.mov", video, CancellationToken.None);
        Assert.Equal(4096, info.SizeBytes);
    }
}
