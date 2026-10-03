// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Previews (<see cref="GpuRunnerOptions.Previews"/>): while it trains, a runner uploads the splats so far at a few steps.
/// Each is stored and checked like a result, then finished and installed on the model by <c>GpuPreviewWorker</c>, so the
/// wall shows a photo-real view long before the training ends. Only the newest pending one is kept (the installed one's
/// file is <see cref="GpuJob.InstalledPreviewPath"/>, never touched here), and the rules of <see cref="GpuJobPreviews"/>
/// hold in every conditional update.
/// </summary>
public sealed partial class GpuJobQueue
{
    private const int RecordAttempts = 3;

    public async Task<RunnerJobOutcome> AcceptPreviewAsync(
        GpuRunner runner, Guid jobId, int step, int total, Stream body, string? contentEncoding, CancellationToken ct)
    {
        var encoding = contentEncoding?.Trim().ToLowerInvariant();
        if (encoding is not (null or "" or "identity" or "gzip"))
        {
            return RunnerJobOutcome.UnsupportedEncoding;
        }

        var (found, job) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok || job is null)
        {
            return found;
        }

        if (!options.Previews || !GpuJobPreviews.MayAccept(job, step, total))
        {
            return RunnerJobOutcome.PreviewRefused;
        }

        using var slot = uploads.TryAcquire(jobId, out var busy);
        if (slot is null)
        {
            return busy;
        }

        if (FreeBytes() is { } free && free < options.MinFreeDiskBytes)
        {
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

        return await RecordPreviewAsync(runner, job, stored, format, step, total, ct);
    }

    /// <summary>Remembers the splat worker's finish job of the preview at <paramref name="step"/>, so a restart resumes it.</summary>
    public async Task SetPreviewFinishJobAsync(Guid jobId, int step, string finishJobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        await db.GpuJobs.Where(j => j.Id == jobId && j.PreviewStep == step)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PreviewFinishJobId, finishJobId), ct);
    }

    /// <summary>
    /// Makes the upload the job's pending preview, replacing (and deleting) the older pending one: conditional on the
    /// pending one read just before, so a preview installed meanwhile (moved off <see cref="GpuJob.PreviewPath"/>) is
    /// never deleted. Also records the view it is meant to replace (<see cref="GpuJob.PreviewBaseSplatId"/>).
    /// </summary>
    private async Task<RunnerJobOutcome> RecordPreviewAsync(
        GpuRunner runner, GpuJob job, string stored, string format, int step, int total, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        long? bytes = files.ResolvePhysicalPath(stored) is { } p ? new FileInfo(p).Length : null;
        for (var attempt = 0; attempt < RecordAttempts; attempt++)
        {
            var pending = await db.GpuJobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.PreviewPath).FirstOrDefaultAsync(ct);
            var view = await CurrentViewAsync(db, job.GeometryModelId, ct);
            var updated = await db.GpuJobs
                .Where(j => j.Id == job.Id && j.ClaimedByRunnerId == runner.Id && j.InstalledAt == null && j.PreviewPath == pending
                            && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running)
                            && (j.PreviewStep == null || j.PreviewStep < step))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.PreviewPath, stored)
                        .SetProperty(j => j.PreviewFormat, format)
                        .SetProperty(j => j.PreviewBytes, bytes)
                        .SetProperty(j => j.PreviewStep, step)
                        .SetProperty(j => j.TotalSteps, total)
                        .SetProperty(j => j.PreviewBaseSplatId, view)
                        .SetProperty(j => j.PreviewFinishJobId, (string?)null),
                    ct);
            if (updated > 0)
            {
                DeleteQuietly(pending, job.Id);
                logger.LogInformation(
                    "Runner {RunnerId} ({Name}) delivered a preview of GPU job {JobId} at step {Step}/{Total}: {Format}, {Bytes} bytes",
                    runner.Id, runner.Name, job.Id, step, total, format, bytes);
                previews?.Enqueue(job.Id);
                return RunnerJobOutcome.Ok;
            }
        }

        files.Delete(stored);
        return RunnerJobOutcome.PreviewRefused;
    }

    private static Task<Guid?> CurrentViewAsync(Data.BlocwerkDbContext db, Guid modelId, CancellationToken ct) =>
        db.WallGeometrySplats.AsNoTracking().Where(s => s.GeometryModelId == modelId).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
}
