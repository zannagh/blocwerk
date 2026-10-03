// <copyright file="WallCaptureProcessor.Video.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Abstractions;
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
    private Task PrepareVideoFramesAsync(Guid captureId, CancellationToken ct) => PrepareVideoFramesAsync(captureId, WallCaptureStatus.Splatting, ct);

    /// <summary>As above, reported under <paramref name="status"/> (the markerless reconstruction extracts them earlier).</summary>
    private async Task PrepareVideoFramesAsync(Guid captureId, WallCaptureStatus status, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var row = await db.WallCaptures.AsNoTracking().Where(c => c.Id == captureId)
            .Select(c => new { c.VideoStoredPath, c.VideoFramesJson }).FirstAsync(ct);
        if (row.VideoStoredPath is not { } video || row.VideoFramesJson is not null || videoFrames is null)
        {
            return;
        }

        // Resumable (no frames stored → extracted again), but a deploy mid-run throws away up to
        // VideoExtractTimeout of ffmpeg work, so the gate stays busy until the frames are stored.
        using var busy = busyGate?.Hold(DeployBusyWork.CaptureVideoFrames);
        await SetStageAsync(captureId, status, status == WallCaptureStatus.Splatting ? 0 : 0.2, VideoStage, ct);
        var names = await ExtractFramesAsync(captureId, video, status, ct);
        await UpdateAsync(captureId, c =>
        {
            c.VideoFramesJson = CaptureVideoFiles.FramesJson(names);
            c.VideoStoredPath = null;
        }, ct);
        files.Delete(video);
        logger.LogInformation("Capture {CaptureId}: {Frames} video frame(s) extracted for the photo-real view", captureId, names.Count);
    }

    /// <summary>
    /// Extracts the frames straight into the capture store, one at a time (a 4K HDR clip's frames would otherwise sit in
    /// memory together); returns their stored names. Frames stored by a run that then fails are deleted again.
    /// </summary>
    private async Task<List<string>> ExtractFramesAsync(Guid captureId, string video, WallCaptureStatus status, CancellationToken ct)
    {
        var path = files.ResolvePhysicalPath(video);
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        var latest = new LatestProgress();
        var request = options.VideoFrameRequest();
        var names = new List<string>();
        var extraction = Task.Run(() => videoFrames!.ExtractAsync(path, request, StoreFrameAsync, latest, ct), ct);
        try
        {
            while (await Task.WhenAny(extraction, Task.Delay(TimeSpan.FromSeconds(2), ct)) != extraction)
            {
                var percent = (latest.Value * 100).ToString("0", CultureInfo.InvariantCulture);
                var progress = status == WallCaptureStatus.Splatting ? VideoBand * latest.Value : 0.2;
                await SetStageAsync(captureId, status, progress, $"{VideoStage} ({percent} %)", ct);
            }

            await extraction;
            return names;
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("Capture {CaptureId}: the video could not be turned into frames ({Reason}); training from the photos only",
                captureId, ex.Message);
            DeleteStoredFrames(names);
            return [];
        }
        catch
        {
            // A failed progress write (or shutdown) leaves the extraction running: let it end before its frames go.
            try
            {
                await extraction;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Capture {CaptureId}: the abandoned frame extraction ended with an error", captureId);
            }

            DeleteStoredFrames(names);
            throw;
        }

        async Task StoreFrameAsync(byte[] frame, CancellationToken token)
        {
            var name = await files.SaveAsync(frame, ".jpg", token);
            lock (names)
            {
                names.Add(name);
            }
        }
    }

    private void DeleteStoredFrames(List<string> names)
    {
        lock (names)
        {
            foreach (var name in names)
            {
                files.Delete(name);
            }

            names.Clear();
        }
    }

    /// <summary>The capture's stored frames as splat-request parts (<c>vf_0001.jpg</c>, … in video order), streamed from disk.</summary>
    private async Task<List<ComputeJobPart>> FramePartsAsync(ComputePhotoParts photoParts, Guid captureId, CancellationToken ct)
    {
        string? json;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            json = await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.VideoFramesJson).FirstAsync(ct);
        }

        var parts = new List<ComputeJobPart>();
        foreach (var name in CaptureVideoFiles.Frames(json))
        {
            if (await photoParts.PartAsync("photos", CaptureSplatDocuments.FrameName(parts.Count + 1), name, asJpeg: true, ct) is { } part)
            {
                parts.Add(part);
            }
        }

        return parts;
    }
}
