// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The trained splat arrives: streamed to the capture store under a size cap (the deploy gate is held while it
/// streams; a gzip body is decoded on the fly and the cap counts the decoded bytes), content-checked, then handed back
/// to the capture pipeline, which finishes it on the server (crop, cleanup, export, level-of-detail ladder) and
/// installs it.
/// </summary>
public sealed partial class GpuJobQueue
{
    private const int MaxStatsBytes = 8 * 1024;

    /// <summary>How often a delivered result is handed back while the splat worker cannot finish it.</summary>
    private static readonly TimeSpan FinishRetryInterval = TimeSpan.FromMinutes(15);

    /// <summary>After this long an unfinished delivered result is given up.</summary>
    private static readonly TimeSpan FinishGiveUp = TimeSpan.FromHours(24);

    public async Task<RunnerJobOutcome> AcceptResultAsync(
        GpuRunner runner, Guid jobId, Stream body, string? contentEncoding, string? statsJson, CancellationToken ct)
    {
        var encoding = contentEncoding?.Trim().ToLowerInvariant();
        if (encoding is not (null or "" or "identity" or "gzip"))
        {
            return RunnerJobOutcome.UnsupportedEncoding;
        }

        var (found, _) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok)
        {
            return found;
        }

        var (stored, refused) = await StoreUploadAsync(runner, jobId, body, encoding == "gzip", ct);
        if (stored is null)
        {
            return refused;
        }

        var format = ValidateStored(stored, runner, jobId);
        if (format is null)
        {
            files.Delete(stored);
            return RunnerJobOutcome.Invalid;
        }

        return await CompleteAsync(runner, jobId, stored, format, CleanStats(statsJson), ct);
    }

    /// <summary>
    /// Hands a delivered but not yet installed result back to the capture pipeline, when there is one (the processor
    /// calls this after it finished a capture as "waiting for a runner", in case the result beat it).
    /// </summary>
    public async Task ResumeIfDeliveredAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var delivered = await db.GpuJobs.AnyAsync(
            j => j.CaptureId == captureId && j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null, ct);
        if (delivered)
        {
            await HandBackAsync(db, captureId, ct);
        }
    }

    /// <summary>The runner's stats header if it is a small flat JSON object; anything else is dropped.</summary>
    internal static string? CleanStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxStatsBytes)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || doc.RootElement.EnumerateObject().Any(p => p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && p.Name != "retries"))
            {
                return null;
            }

            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(string? Stored, RunnerJobOutcome Refused)> StoreUploadAsync(
        GpuRunner runner, Guid jobId, Stream body, bool gzip, CancellationToken ct)
    {
        using var hold = busyGate?.Hold(DeployBusyWork.RunnerResultUpload);
        await using var decoded = gzip ? new GZipStream(body, CompressionMode.Decompress, leaveOpen: true) : null;
        try
        {
            return (await files.SaveStreamAsync(decoded ?? body, ".upl", options.MaxResultBytes, ct), RunnerJobOutcome.Ok);
        }
        catch (CaptureFileTooLargeException)
        {
            logger.LogWarning("Runner {RunnerId} sent a result over {Max} bytes for GPU job {JobId}", runner.Id, options.MaxResultBytes, jobId);
            return (null, RunnerJobOutcome.TooLarge);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("Runner {RunnerId} sent an unreadable gzip body for GPU job {JobId}: {Reason}", runner.Id, jobId, ex.Message);
            return (null, RunnerJobOutcome.Invalid);
        }
    }

    private string? ValidateStored(string stored, GpuRunner runner, Guid jobId)
    {
        try
        {
            var path = files.ResolvePhysicalPath(stored) ?? throw new InvalidDataException("The upload was not stored.");
            return SplatResultFormat.Validate(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            logger.LogWarning("Runner {RunnerId} sent an invalid result for GPU job {JobId}: {Reason}", runner.Id, jobId, ex.Message);
            return null;
        }
    }

    private async Task<RunnerJobOutcome> CompleteAsync(
        GpuRunner runner, Guid jobId, string stored, string format, string? stats, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.ClaimedByRunnerId == runner.Id, ct);
        if (job is null || job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running))
        {
            // Cancelled or requeued while the upload streamed.
            files.Delete(stored);
            return job is null ? RunnerJobOutcome.NotYours : RunnerJobOutcome.Gone;
        }

        job.Status = GpuJobStatus.Succeeded;
        job.ResultPath = stored;
        job.ResultFormat = format;
        job.ResultBytes = files.ResolvePhysicalPath(stored) is { } p ? new FileInfo(p).Length : null;
        job.ResultStatsJson = stats;
        job.Progress = 1;
        job.Stage = "trained; finishing on the server";
        job.LeaseExpiresAt = Now + FinishRetryInterval;
        job.CompletedAt = Now;
        job.Error = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Runner {RunnerId} ({Name}) delivered GPU job {JobId}: {Format}, {Bytes} bytes",
            runner.Id, runner.Name, job.Id, format, job.ResultBytes);
        await HandBackAsync(db, job.CaptureId, ct);
        return RunnerJobOutcome.Ok;
    }

    /// <summary>
    /// The capture (finished as "Model ready" while the runner trained) goes back to the photo-real stage, exactly
    /// like a retrain: its model and any older view stay live until the new view is stored.
    /// </summary>
    private async Task HandBackAsync(BlocwerkDbContext db, Guid captureId, CancellationToken ct)
    {
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == captureId, ct);
        if (capture is { Status: WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutTextures or WallCaptureStatus.SucceededWithoutSplat })
        {
            capture.Error = WallCaptureService.TexturePart(capture.Error);
            capture.Status = WallCaptureStatus.Splatting;
            capture.Stage = "Photo-real view: finishing on the server";
            capture.Progress = 0.96;
            capture.Attempts = 0;
            capture.CompletedAt = null;
            await db.SaveChangesAsync(ct);
        }
        else if (capture is not { Status: WallCaptureStatus.Splatting })
        {
            return;
        }

        captureQueue.Enqueue(captureId);
    }
}
