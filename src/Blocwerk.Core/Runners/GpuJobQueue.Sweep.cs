// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>Lease expiry, cancellation, and the "waiting for a 3D runner" text of queued jobs.</summary>
public sealed partial class GpuJobQueue
{
    /// <summary>Requeues (or fails) every job whose lease ran out; refreshes the queued jobs' text. Returns how many expired.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var now = Now;
        var expired = await db.GpuJobs
            .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            logger.LogWarning(
                "GPU job {JobId}: the lease of runner {RunnerId} expired (lost {Lost} of {Max})",
                job.Id, job.ClaimedByRunnerId, job.LostLeaseCount + 1, options.MaxLostLeases);
            await ReleaseAsync(db, job, ReleaseKind.LostLease, "the 3D runner stopped responding (its lease expired)", ct);
        }

        await RetryUnfinishedAsync(db, now, ct);
        await RefreshWaitingAsync(db, ct);
        return expired.Count;
    }

    /// <summary>Cancels the capture's waiting or running GPU job. The capture keeps its model (and any older photo-real view).</summary>
    public async Task<bool> CancelForCaptureAsync(Guid captureId, string reason, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var jobs = await CancelActiveAsync(db, captureId, reason, Now, ct);
        await db.SaveChangesAsync(ct);
        foreach (var job in jobs)
        {
            logger.LogInformation("GPU job {JobId} of capture {CaptureId} cancelled: {Reason}", job.Id, captureId, reason);
            DeleteFiles(job);
        }

        return jobs.Count > 0;
    }

    /// <summary>
    /// Marks the capture's waiting, running or delivered-but-not-installed jobs cancelled (not saved) and returns them, so the caller can delete
    /// their files after saving. A runner holding one learns it with its next call (410).
    /// </summary>
    public static async Task<List<GpuJob>> CancelActiveAsync(
        BlocwerkDbContext db, Guid captureId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var jobs = await db.GpuJobs
            .Where(j => j.CaptureId == captureId
                        && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                            || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null)))
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Status = GpuJobStatus.Cancelled;
            job.Error = Clip(reason, 2048);
            job.CompletedAt = now;
            job.LeaseExpiresAt = null;
        }

        return jobs;
    }

    /// <summary>Requeues whatever a revoked runner held, right away and at no cost (its key already stopped working).</summary>
    public async Task ReleaseRunnerAsync(Guid runnerId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var held = await db.GpuJobs
            .Where(j => j.ClaimedByRunnerId == runnerId && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .ToListAsync(ct);
        foreach (var job in held)
        {
            await ReleaseAsync(db, job, ReleaseKind.Free, "the 3D runner was revoked", ct);
        }
    }

    /// <summary>
    /// Delivered results the splat worker could not finish yet (it was unreachable): handed back to the capture pipeline
    /// every <see cref="FinishRetryInterval"/>, and given up after <see cref="FinishGiveUp"/>.
    /// </summary>
    private async Task RetryUnfinishedAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.GpuJobs
            .Where(j => j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in due)
        {
            if (job.CompletedAt < now - FinishGiveUp)
            {
                logger.LogWarning("GPU job {JobId}: the delivered result could not be finished for a day; giving up", job.Id);
                job.Status = GpuJobStatus.Failed;
                job.Error = "the trained view could not be finished on the server (the splat worker was unreachable)";
                await db.SaveChangesAsync(ct);
                DeleteFiles(job);
                continue;
            }

            job.LeaseExpiresAt = now + FinishRetryInterval;
            await db.SaveChangesAsync(ct);
            await HandBackAsync(db, job.CaptureId, ct);
        }
    }

    /// <summary>
    /// A job that failed for good: a finished capture whose view it was to be says so (Model ready, no photo-real view),
    /// exactly like a failed training on the splat worker. A capture that moved on (a retrain) is left alone.
    /// </summary>
    private static async Task MarkCaptureWithoutSplatAsync(BlocwerkDbContext db, Guid captureId, string reason, CancellationToken ct)
    {
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == captureId, ct);
        if (capture is { Status: WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutTextures })
        {
            WallCaptureProcessor.EndWithoutSplat(capture, reason);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>"waiting for a 3D runner (none online)" / "(1 online, busy)" on every queued job.</summary>
    private async Task RefreshWaitingAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var waiting = await db.GpuJobs.Where(j => j.Status == GpuJobStatus.Queued).ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return;
        }

        var online = Now - options.OnlineWindow;
        var shared = await db.GpuRunners.Where(r => r.RevokedAt == null && r.SharedWithOtherWalls && r.LastSeenAt >= online)
            .Select(r => r.MaxQuality).ToListAsync(ct);
        var walls = waiting.Select(j => j.WallId).Distinct().ToList();
        var own = await Assignments(db).Where(rw => walls.Contains(rw.WallId) && rw.Runner.LastSeenAt >= online)
            .Select(rw => new { rw.WallId, rw.Runner.MaxQuality }).ToListAsync(ct);
        var accepting = options.SharedNeedsOptIn
            ? await db.GpuRunnerSharedOptIns.Where(o => walls.Contains(o.WallId)).Select(o => o.WallId).ToListAsync(ct)
            : walls;
        foreach (var job in waiting)
        {
            var able = own.Where(o => o.WallId == job.WallId).Select(o => o.MaxQuality)
                .Concat(accepting.Contains(job.WallId) ? shared : [])
                .Count(q => QualityCap(q, null) >= job.Quality);
            job.Stage = able == 0
                ? $"waiting for a 3D runner that can train {CaptureSplatDocuments.QualityName(job.Quality)} (none online)"
                : $"waiting for a 3D runner ({able} online, busy)";
        }

        await db.SaveChangesAsync(ct);
    }
}
