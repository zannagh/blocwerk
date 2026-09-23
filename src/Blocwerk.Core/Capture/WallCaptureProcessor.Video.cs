// <copyright file="WallCaptureProcessor.Video.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The walk-along video's part of the photo-real stage: right before the splat job is submitted the
/// video becomes frames (<see cref="ICaptureVideoFrameExtractor"/>), stored next to the photos under
/// the same retention, and the video itself is deleted. The frames ride along in the splat request
/// as auxiliary images named <see cref="CaptureSplatDocuments.FramePrefix"/>…: the worker matches
/// them sequentially and against the photos, but aligns the scene to the wall model with the
/// PHOTOS' solved cameras only. They never were capture photos, so detection, solve, the photo limit
/// and the panel-photo picker never see them.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>Share of the photo-real stage's progress the frame extraction takes.</summary>
    internal const double VideoBand = 0.05;

    private const string VideoStage = "Photo-real view: extracting video frames";

    /// <summary>
    /// Extracts the frames once (a resumed capture whose frames exist skips this). A video that
    /// cannot be read costs only its frames: the splat is then trained from the photos alone.
    /// </summary>
    private async Task PrepareVideoFramesAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var row = await db.WallCaptures.AsNoTracking().Where(c => c.Id == captureId)
            .Select(c => new { c.VideoStoredPath, c.VideoFramesJson }).FirstAsync(ct);
        if (row.VideoStoredPath is not { } video || row.VideoFramesJson is not null || videoFrames is null)
        {
            return;
        }

        await SetStageAsync(captureId, WallCaptureStatus.Splatting, 0, VideoStage, ct);
        var frames = await ExtractFramesAsync(captureId, video, ct);
        var names = new List<string>(frames.Count);
        foreach (var frame in frames)
        {
            names.Add(await files.SaveAsync(frame, ".jpg", ct));
        }

        await UpdateAsync(captureId, c =>
        {
            c.VideoFramesJson = CaptureVideoFiles.FramesJson(names);
            c.VideoStoredPath = null;
        }, ct);
        files.Delete(video);
        logger.LogInformation("Capture {CaptureId}: {Frames} video frame(s) extracted for the photo-real view", captureId, names.Count);
    }

    private async Task<IReadOnlyList<byte[]>> ExtractFramesAsync(Guid captureId, string video, CancellationToken ct)
    {
        var path = files.ResolvePhysicalPath(video);
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        var latest = new LatestProgress();
        var request = new CaptureVideoFrameRequest(options.VideoFramesPerSecond, options.MaxVideoFrames, options.VideoExtractTimeout);
        var extraction = Task.Run(() => videoFrames!.ExtractAsync(path, request, latest, ct), ct);
        while (await Task.WhenAny(extraction, Task.Delay(TimeSpan.FromSeconds(2), ct)) != extraction)
        {
            var percent = (latest.Value * 100).ToString("0", CultureInfo.InvariantCulture);
            await SetStageAsync(captureId, WallCaptureStatus.Splatting, VideoBand * latest.Value, $"{VideoStage} ({percent} %)", ct);
        }

        try
        {
            return await extraction;
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("Capture {CaptureId}: the video could not be turned into frames ({Reason}); training from the photos only",
                captureId, ex.Message);
            return [];
        }
    }

    /// <summary>The capture's stored frames as splat-request parts (<c>vf_0001.jpg</c>, … in video order).</summary>
    private async Task<List<ComputeJobPart>> FramePartsAsync(Guid captureId, CancellationToken ct)
    {
        string? json;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            json = await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.VideoFramesJson).FirstAsync(ct);
        }

        var parts = new List<ComputeJobPart>();
        foreach (var name in CaptureVideoFiles.Frames(json))
        {
            var bytes = await files.ReadAsync(name, ct);
            if (bytes is null)
            {
                continue;
            }

            parts.Add(ComputeJobPart.File(
                "photos", CaptureSplatDocuments.FrameName(parts.Count + 1) + ".jpg", ImageMetadataStripper.Strip(bytes), "image/jpeg"));
        }

        return parts;
    }
}
