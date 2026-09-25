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
/// The trained splat arrives: one upload per job and <see cref="GpuRunnerOptions.MaxConcurrentUploads"/> server-wide,
/// only while the capture store has <see cref="GpuRunnerOptions.MinFreeDiskBytes"/> free. It is streamed to the capture
/// store under a size cap (the deploy gate is held while it streams; a gzip body is decoded on the fly, the cap counts
/// the decoded bytes, and a body that inflates past a sane ratio is stopped at once), content-checked, then handed
/// back to the capture pipeline, which finishes it on the server (crop, cleanup, export, level-of-detail ladder) and
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

        using var slot = uploads.TryAcquire(jobId, out var busy);
        if (slot is null)
        {
            logger.LogWarning("Runner {RunnerId} upload for GPU job {JobId} refused: {Reason}", runner.Id, jobId, busy);
            return busy;
        }

        if (FreeBytes() is { } free && free < options.MinFreeDiskBytes)
        {
            logger.LogWarning("Runner {RunnerId} upload for GPU job {JobId} refused: only {Free} bytes free", runner.Id, jobId, free);
            return RunnerJobOutcome.InsufficientStorage;
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

    /// <summary>Free bytes of the capture store's drive, or null when unknown.</summary>
    private long? FreeBytes() =>
        files.ResolvePhysicalPath("probe") is { } probe && Path.GetDirectoryName(probe) is { } dir ? disk.FreeBytes(dir) : null;

    private async Task<(string? Stored, RunnerJobOutcome Refused)> StoreUploadAsync(
        GpuRunner runner, Guid jobId, Stream body, bool gzip, CancellationToken ct)
    {
        using var hold = busyGate?.Hold(DeployBusyWork.RunnerResultUpload);
        await using var encoded = gzip ? new CountingReadStream(body) : null;
        await using var decoded = encoded is null ? null : new GZipStream(encoded, CompressionMode.Decompress, leaveOpen: true);
        await using var guarded = new GuardedUploadStream(decoded ?? body, encoded, options, FreeBytes);
        try
        {
            return (await files.SaveStreamAsync(guarded, ".upl", options.MaxResultBytes, ct), RunnerJobOutcome.Ok);
        }
        catch (CaptureFileTooLargeException)
        {
            logger.LogWarning("Runner {RunnerId} sent a result over {Max} bytes for GPU job {JobId}", runner.Id, options.MaxResultBytes, jobId);
            return (null, RunnerJobOutcome.TooLarge);
        }
        catch (RunnerUploadRefusedException ex)
        {
            logger.LogWarning("Runner {RunnerId} upload for GPU job {JobId} stopped: {Reason}", runner.Id, jobId, ex.Message);
            return (null, ex.Outcome);
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
            return SplatResultFormat.Validate(path, options.MaxResultSplats);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or OverflowException)
        {
            logger.LogWarning("Runner {RunnerId} sent an invalid result for GPU job {JobId}: {Reason}", runner.Id, jobId, ex.Message);
            return null;
        }
    }

    /// <summary>Conditional: only a job this runner still holds becomes Succeeded (a sweep or a cancel may have won the race).</summary>
    private async Task<RunnerJobOutcome> CompleteAsync(
        GpuRunner runner, Guid jobId, string stored, string format, string? stats, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var now = Now;
        var retryAt = now + FinishRetryInterval;
        long? bytes = files.ResolvePhysicalPath(stored) is { } p ? new FileInfo(p).Length : null;
        var updated = await db.GpuJobs
            .Where(j => j.Id == jobId && j.ClaimedByRunnerId == runner.Id
                        && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Succeeded)
                    .SetProperty(j => j.ResultPath, stored)
                    .SetProperty(j => j.ResultFormat, format)
                    .SetProperty(j => j.ResultBytes, bytes)
                    .SetProperty(j => j.ResultStatsJson, stats)
                    .SetProperty(j => j.Progress, 1)
                    .SetProperty(j => j.Stage, "trained; finishing on the server")
                    .SetProperty(j => j.LeaseExpiresAt, retryAt)
                    .SetProperty(j => j.CompletedAt, now)
                    .SetProperty(j => j.Error, (string?)null),
                ct);
        if (updated == 0)
        {
            // Cancelled, requeued or taken away while the upload streamed.
            files.Delete(stored);
            return RunnerJobOutcome.Gone;
        }

        var captureId = await db.GpuJobs.Where(j => j.Id == jobId).Select(j => j.CaptureId).FirstAsync(ct);
        logger.LogInformation(
            "Runner {RunnerId} ({Name}) delivered GPU job {JobId}: {Format}, {Bytes} bytes", runner.Id, runner.Name, jobId, format, bytes);
        await HandBackAsync(db, captureId, ct);
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
