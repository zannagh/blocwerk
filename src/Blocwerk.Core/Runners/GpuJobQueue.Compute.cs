// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Splat prepare / finish jobs (<see cref="GpuJobKind.SplatPrepare"/>, <see cref="GpuJobKind.SplatFinish"/>): the splat worker's
/// CPU halves run by a 3D runner. They share the queue, lease, claim token, retry budgets and uploads; what differs lives here:
/// the input and the result are zips of the runner envelope format (<see cref="RunnerComputeEnvelope"/>,
/// <see cref="RunnerComputeResult"/>), the memory a job needs defaults per kind, lifetimes differ for a markerless capture (which
/// is blocked while its reconstruction waits), and a delivered or ended job wakes the capture that is parked on it. Nothing
/// produces these jobs unless <see cref="GpuRunnerOptions.SplatOnRunners"/> is on; while it is off the sweep cancels any queued.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>The largest result upload of the job (its kind's cap); the default cap for an unknown job.</summary>
    public async Task<long> UploadLimitAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var kind = await db.GpuJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => (GpuJobKind?)j.Kind).FirstOrDefaultAsync(ct);
        return kind is { } k ? options.MaxUploadBytes(k) : options.MaxResultBytes;
    }

    /// <summary>
    /// A prepare / finish job is over once the pipeline has read its result (<paramref name="error"/> null: installed) or given up
    /// on it: the input envelope (photos or the trained scene, gigabytes) goes at once; the result stays while something may still
    /// read it (the sweep and the capture retention remove it), unless the job failed or <paramref name="dropResult"/> (nothing
    /// reads it again). <paramref name="cancelled"/>: the server no longer wants a job that has not ended; it ends Cancelled.
    /// </summary>
    public async Task CloseComputeAsync(Guid jobId, string? error, CancellationToken ct, bool cancelled = false, bool dropResult = false)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.FirstOrDefaultAsync(
            j => j.Id == jobId && (j.Kind == GpuJobKind.SplatPrepare || j.Kind == GpuJobKind.SplatFinish), ct);
        if (job is null)
        {
            return;
        }

        var spent = new[] { job.BundlePath, job.PreparedPath }.Distinct().ToList();
        var ended = error is not null && job.Status is not (GpuJobStatus.Failed or GpuJobStatus.Cancelled);
        if (error is null)
        {
            job.InstalledAt = Now;
            job.Error = null;
        }
        else
        {
            job.Status = job.Status == GpuJobStatus.Cancelled || cancelled ? GpuJobStatus.Cancelled : GpuJobStatus.Failed;
            job.Error = Clip(error, 2048);
            job.CompletedAt ??= Now;
        }

        if ((error is not null || dropResult) && job.ResultPath is { } result)
        {
            spent.Add(result);
            job.ResultPath = null;
            job.LeftoverDroppedAt = error is null ? Now : job.LeftoverDroppedAt;
        }

        await db.SaveChangesAsync(ct);
        DeleteAll(spent, job.Id);
        if (ended)
        {
            PulseJob(job.WallId, job.Id, null, ended: true);
        }
    }

    private TimeSpan ShortestQueuedLifetime =>
        options.MarkerlessQueuedLifetime < options.QueuedLifetime ? options.MarkerlessQueuedLifetime : options.QueuedLifetime;

    /// <summary>
    /// Sets what a prepare / finish job always has (draft quality; the memory its kind needs unless the submitter named more),
    /// so the claim query is one comparison for every job.
    /// </summary>
    private void NormalizeCompute(GpuJob job)
    {
        if (job.Kind is not (GpuJobKind.SplatPrepare or GpuJobKind.SplatFinish))
        {
            return;
        }

        job.Quality = SplatQuality.Draft;
        job.RequiredMemoryMb ??= job.Kind == GpuJobKind.SplatPrepare ? options.PrepareMemoryMb : options.FinishMemoryMb;
    }

    /// <summary>
    /// The delivered result waits for the pipeline: the parked capture is woken again every retry interval, and the job is given
    /// up after a day.
    /// </summary>
    private async Task RetryComputeDeliveryAsync(BlocwerkDbContext db, GpuJob job, DateTimeOffset now, CancellationToken ct)
    {
        if (job.CompletedAt < now - FinishGiveUp)
        {
            logger.LogWarning("GPU job {JobId}: the delivered result was not read for a day; giving up", job.Id);
            await CloseComputeAsync(job.Id, "the 3D runner's result was not picked up on the server within a day", ct);
        }
        else
        {
            await db.GpuJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, now + FinishRetryInterval), ct);
        }

        await HandBackAsync(db, job.CaptureId, job.Kind, ct);
    }

    /// <summary>
    /// With <see cref="GpuRunnerOptions.SplatOnRunners"/> off (a rollback, or never on) no pipeline reads these jobs any more and
    /// their input is a copy of a capture's photos: every queued one is cancelled and its files deleted, and a delivered one that
    /// was never read is closed (it would otherwise wake its capture every retry interval for a day).
    /// </summary>
    private async Task CancelDisabledComputeJobsAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (options.SplatOnRunners)
        {
            return;
        }

        var queued = await db.GpuJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued && (j.Kind == GpuJobKind.SplatPrepare || j.Kind == GpuJobKind.SplatFinish)).ToListAsync(ct);
        const string reason = "3D runners no longer prepare or finish photo-real views on this server";
        foreach (var job in queued)
        {
            var cancelled = await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == GpuJobStatus.Queued)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, GpuJobStatus.Cancelled).SetProperty(j => j.Error, reason).SetProperty(j => j.CompletedAt, now),
                    ct);
            if (cancelled == 0)
            {
                continue;
            }

            PulseJob(job.WallId, job.Id, null, ended: true);
            logger.LogInformation("GPU job {JobId} of capture {CaptureId} cancelled: {Reason}", job.Id, job.CaptureId, reason);
            await JobEndedAsync(db, job, reason, ct);
            DeleteAll(FilesOf(job), job.Id);
        }

        var delivered = await db.GpuJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null && (j.Kind == GpuJobKind.SplatPrepare || j.Kind == GpuJobKind.SplatFinish))
            .Select(j => j.Id).ToListAsync(ct);
        foreach (var id in delivered)
        {
            await CloseComputeAsync(id, reason, ct);
        }
    }

    /// <summary>The captures among the prepare jobs of <paramref name="jobs"/> that reconstruct without markers.</summary>
    private static async Task<HashSet<Guid>> MarkerlessCapturesAsync(BlocwerkDbContext db, IEnumerable<GpuJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Where(j => j.Kind == GpuJobKind.SplatPrepare).Select(j => j.CaptureId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var markerless = await db.WallCaptures.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.GeometryMode == WallCaptureGeometryMode.Features).Select(c => c.Id).ToListAsync(ct);
        return markerless.ToHashSet();
    }

    /// <summary>How long a queued job may wait for a runner: a markerless capture's reconstruction blocks the wall, so it is shorter.</summary>
    private TimeSpan QueuedLifetimeOf(GpuJob job, HashSet<Guid> markerless) =>
        job.Kind == GpuJobKind.SplatPrepare && markerless.Contains(job.CaptureId) ? options.MarkerlessQueuedLifetime : options.QueuedLifetime;

    private string? ValidateStoredCompute(string stored, GpuRunner runner, Guid jobId, long maxBytes)
    {
        try
        {
            var path = files.ResolvePhysicalPath(stored) ?? throw new InvalidDataException("The upload was not stored.");
            RunnerComputeResult.Validate(path, maxBytes);
            return RunnerComputeResult.Format;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            logger.LogWarning("Runner {RunnerId} sent an invalid result for GPU job {JobId}: {Reason}", runner.Id, jobId, ex.Message);
            return null;
        }
    }
}
