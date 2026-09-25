// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>Cancelling jobs: by an admin, by a retrain, by photo retention, by a revoke, and when runners are turned off.</summary>
public sealed partial class GpuJobQueue
{
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
    public static Task<List<GpuJob>> CancelActiveAsync(
        BlocwerkDbContext db, Guid captureId, string reason, DateTimeOffset now, CancellationToken ct) =>
        CancelActiveAsync(db, [captureId], reason, now, ct);

    /// <summary><see cref="CancelActiveAsync(BlocwerkDbContext, Guid, string, DateTimeOffset, CancellationToken)"/> for several captures.</summary>
    public static async Task<List<GpuJob>> CancelActiveAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> captureIds, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var jobs = await db.GpuJobs
            .Where(j => captureIds.Contains(j.CaptureId)
                        && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                            || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null)))
            .ToListAsync(ct);
        MarkCancelled(jobs, reason, now);
        return jobs;
    }

    /// <summary>
    /// With <see cref="GpuRunnerMode.Off"/> (checked at startup): every waiting or running job is cancelled and its
    /// files deleted. Nothing could claim or finish them any more (the runner API is not even mapped), and their
    /// bundles are copies of capture photos. Returns how many were cancelled.
    /// </summary>
    public async Task<int> CancelAllActiveAsync(string reason, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var jobs = await db.GpuJobs
            .Where(j => j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running)
            .ToListAsync(ct);
        if (jobs.Count == 0)
        {
            return 0;
        }

        MarkCancelled(jobs, reason, Now);
        await db.SaveChangesAsync(ct);
        foreach (var job in jobs)
        {
            await MarkCaptureWithoutSplatAsync(db, job.CaptureId, reason, ct);
            DeleteFiles(job);
        }

        logger.LogInformation("3D runners are off: cancelled {Count} waiting or running GPU job(s)", jobs.Count);
        return jobs.Count;
    }

    /// <summary>Requeues whatever a revoked runner held, right away and at no cost (its key already stopped working).</summary>
    public async Task ReleaseRunnerAsync(Guid runnerId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var held = await db.GpuJobs.AsNoTracking()
            .Where(j => j.ClaimedByRunnerId == runnerId && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .ToListAsync(ct);
        foreach (var job in held)
        {
            await ReleaseAsync(db, job, ReleaseKind.Free, "the 3D runner was revoked", ct);
        }
    }

    private static void MarkCancelled(IEnumerable<GpuJob> jobs, string reason, DateTimeOffset now)
    {
        foreach (var job in jobs)
        {
            job.Status = GpuJobStatus.Cancelled;
            job.Error = Clip(reason, 2048);
            job.CompletedAt = now;
            job.LeaseExpiresAt = null;
        }
    }
}
