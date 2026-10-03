// <copyright file="CaptureVideoRetentionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A capture's video frames (and a video never turned into frames) follow its photos' retention.</summary>
public class CaptureVideoRetentionTests
{
    [Fact]
    public async Task ExpiredCapture_LosesItsVideoAndFrames_TheActiveModelsAreKept_AndReferencedFramesAreNoOrphans()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var active = await s.StartCaptureAsync(beforeStart: async id =>
        {
            await using var video = FakeVideoFrameExtractor.Mp4Stream();
            await s.Service.AddVideoAsync(id, "walk.mp4", video, CancellationToken.None);
        });
        await s.Processor.ProcessAsync(active, CancellationToken.None);
        var old = await AddOldCaptureWithVideoAsync(h, s);
        foreach (var file in s.Files.ListFiles())
        {
            File.SetLastWriteTimeUtc(s.Files.ResolvePhysicalPath(file.Name)!, DateTime.UtcNow.AddHours(-3));
        }

        await new WallCaptureSweeper(h.RootContextFactory, s.Files, s.Options, NullLogger<WallCaptureSweeper>.Instance)
            .SweepAsync(CancellationToken.None);

        await using var db = h.CreateContext();
        var oldRow = await db.WallCaptures.SingleAsync(c => c.Id == old);
        Assert.Null(oldRow.VideoStoredPath);
        Assert.Null(oldRow.VideoFramesJson);
        var activeFrames = CaptureVideoFiles.Frames(await db.WallCaptures.Where(c => c.Id == active).Select(c => c.VideoFramesJson).SingleAsync());
        Assert.Equal(5, activeFrames.Count);
        var left = s.Files.ListFiles().Select(f => f.Name).ToHashSet();
        Assert.All(activeFrames, f => Assert.Contains(f, left));
        Assert.DoesNotContain(left, n => n.EndsWith(".mp4", StringComparison.Ordinal));
    }

    private static async Task<Guid> AddOldCaptureWithVideoAsync(WallTestHarness h, CaptureScenario s)
    {
        var frames = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            frames.Add(await s.Files.SaveAsync(CaptureScenario.TinyJpeg(50 + i), ".jpg", CancellationToken.None));
        }

        await using var db = h.CreateContext();
        var capture = new WallCapture
        {
            WallId = h.WallId,
            CreatedByUserId = h.ActingUser.Id,
            Status = WallCaptureStatus.SucceededWithoutSplat,
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-31),
            VideoStoredPath = await s.Files.SaveAsync([0, 0, 0, 24], ".mp4", CancellationToken.None),
            VideoFramesJson = CaptureVideoFiles.FramesJson(frames),
        };
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        return capture.Id;
    }
}
