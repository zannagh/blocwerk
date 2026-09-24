// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The trained splat arrives: streamed to the capture store under a size cap (the deploy gate is
/// held while it streams), content-checked, then handed back to the capture pipeline, which finishes
/// it on the server (crop, cleanup, export, level-of-detail ladder) and installs it.
/// </summary>
public sealed partial class GpuJobQueue
{
    private const int MaxStatsBytes = 8 * 1024;

    public async Task<RunnerJobOutcome> AcceptResultAsync(
        GpuRunner runner, Guid jobId, Stream body, string? statsJson, CancellationToken ct)
    {
        var (found, _) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok)
        {
            return found;
        }

        string stored;
        using (busyGate?.Hold(DeployBusyWork.RunnerResultUpload))
        {
            try
            {
                stored = await files.SaveStreamAsync(body, ".upl", options.MaxResultBytes, ct);
            }
            catch (CaptureFileTooLargeException)
            {
                logger.LogWarning("Runner {RunnerId} sent a result over {Max} bytes for GPU job {JobId}", runner.Id, options.MaxResultBytes, jobId);
                return RunnerJobOutcome.TooLarge;
            }
        }

        var format = ValidateStored(stored, runner, jobId);
        if (format is null)
        {
            files.Delete(stored);
            return RunnerJobOutcome.Invalid;
        }

        return await CompleteAsync(runner, jobId, stored, format, CleanStats(statsJson), ct);
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
        job.Stage = "trained";
        job.LeaseExpiresAt = null;
        job.CompletedAt = Now;
        job.Error = null;
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == job.CaptureId, ct);
        var resume = capture is { Status: WallCaptureStatus.AwaitingRunner };
        if (resume)
        {
            capture!.Status = WallCaptureStatus.Splatting;
            capture.Stage = "Photo-real view: finishing on the server";
            capture.Progress = 0.96;
            capture.Attempts = 0;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Runner {RunnerId} ({Name}) delivered GPU job {JobId}: {Format}, {Bytes} bytes",
            runner.Id, runner.Name, job.Id, format, job.ResultBytes);
        if (resume)
        {
            captureQueue.Enqueue(job.CaptureId);
        }

        return RunnerJobOutcome.Ok;
    }
}
