// <copyright file="WallCaptureService.Video.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The optional walk-along video of a draft. It is NOT a capture photo: it never reaches marker
/// detection, the solve or the panel photos, and does not count against the photo limit. The
/// pipeline turns it into frames for the photo-real stage only (<c>WallCaptureProcessor.Video</c>).
/// </summary>
public sealed partial class WallCaptureService
{
    private WallCapturePipelineOptions PipelineOptions => pipelineOptions ?? new WallCapturePipelineOptions();

    public async Task<CaptureVideoInfo> AddVideoAsync(Guid captureId, string? fileName, Stream content, CancellationToken ct)
    {
        var name = fileName is { Length: > 256 } ? fileName[..256] : fileName;
        var extension = Path.GetExtension(name ?? string.Empty);
        if (!CaptureVideoFiles.Extensions.Contains(extension))
        {
            throw new InvalidOperationException($"{name ?? "The file"} is not an MP4 or MOV video.");
        }

        // Every gate BEFORE a single byte is written: admin of the wall, no kiosk, own open draft.
        await EnsureVideoAllowedAsync(captureId);

        // A 1–2 GB upload runs for many minutes and cannot be resumed: hold the deploy gate until the
        // file is stored, probed and attached (or refused, failed or cancelled — the using releases it).
        using var busy = busyGate?.Hold(DeployBusyWork.CaptureVideoUpload);
        var stored = await SaveVideoAsync(content, extension, ct);
        CaptureVideoProbe probe;
        try
        {
            probe = await CheckVideoAsync(stored, name, ct);
        }
        catch
        {
            files.Delete(stored);
            throw;
        }

        return await AttachVideoAsync(captureId, stored, name, probe, ct);
    }

    public async Task RemoveVideoAsync(Guid captureId)
    {
        var (db, _, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            var old = capture.VideoStoredPath;
            capture.VideoStoredPath = null;
            capture.VideoFileName = null;
            capture.VideoSizeBytes = null;
            capture.VideoDurationSeconds = null;
            await db.SaveChangesAsync();
            files.Delete(old);
        }
    }

    private async Task EnsureVideoAllowedAsync(Guid captureId)
    {
        var (db, _, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            if (!await db.Walls.Where(w => w.Id == capture.WallId).Select(w => w.GlyphsEnabled).FirstOrDefaultAsync())
            {
                throw new InvalidOperationException("Switch on printed markers for this wall first.");
            }

            if (!IsSplatConfigured)
            {
                throw new InvalidOperationException("The photo-real view is not set up on this server, so a video would not be used.");
            }
        }
    }

    private async Task<string> SaveVideoAsync(Stream content, string extension, CancellationToken ct)
    {
        try
        {
            return await files.SaveStreamAsync(content, extension, PipelineOptions.MaxVideoBytes, ct);
        }
        catch (CaptureFileTooLargeException ex)
        {
            throw new InvalidOperationException(
                $"The video is larger than {ex.MaxBytes / (1024 * 1024)} MB. A 30–90 second walk is enough; "
                + "record at 1080p or trim it.");
        }
    }

    private async Task<CaptureVideoProbe> CheckVideoAsync(string stored, string? name, CancellationToken ct)
    {
        var path = files.ResolvePhysicalPath(stored) ?? throw new InvalidOperationException("The video could not be stored.");
        var head = new byte[12];
        await using (var stream = File.OpenRead(path))
        {
            await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        }

        if (!CaptureVideoFiles.LooksLikeIsoMedia(head))
        {
            throw new InvalidOperationException($"{name ?? "The file"} is not an MP4 or MOV video.");
        }

        CaptureVideoProbe probe;
        try
        {
            probe = await videoFrames!.ProbeAsync(path, ct);
        }
        catch (InvalidDataException ex)
        {
            logger.LogInformation("Capture video {Name} could not be probed: {Reason}", name, ex.Message);
            throw new InvalidOperationException($"{name ?? "The video"} could not be read as a video.");
        }

        if (probe.DurationSeconds > CaptureVideoFiles.MaxDuration.TotalSeconds)
        {
            throw new InvalidOperationException(
                $"{name ?? "The video"} is longer than {CaptureVideoFiles.MaxDuration.TotalMinutes:0} minutes; a 30–90 second walk is enough.");
        }

        return probe;
    }

    private async Task<CaptureVideoInfo> AttachVideoAsync(
        Guid captureId, string stored, string? name, CaptureVideoProbe probe, CancellationToken ct)
    {
        var size = new FileInfo(files.ResolvePhysicalPath(stored)!).Length;
        string? old;
        try
        {
            var (db, _, capture) = await OpenDraftAsync(captureId);
            await using (db)
            {
                old = capture.VideoStoredPath;
                capture.VideoStoredPath = stored;
                capture.VideoFileName = name;
                capture.VideoSizeBytes = size;
                capture.VideoDurationSeconds = Math.Round(probe.DurationSeconds, 2);
                await db.SaveChangesAsync(ct);
            }
        }
        catch
        {
            files.Delete(stored);
            throw;
        }

        files.Delete(old);
        logger.LogInformation(
            "Capture {CaptureId}: walk-along video stored ({Bytes} bytes, {Seconds:0.#} s)", captureId, size, probe.DurationSeconds);
        return new CaptureVideoInfo(name, size, probe.DurationSeconds);
    }
}
