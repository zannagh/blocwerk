// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>Lease expiry, the queued jobs' lifetime, and the "waiting for a 3D runner" text of queued jobs.</summary>
public sealed partial class GpuJobQueue
{
    /// <summary>
    /// Requeues (or fails) every job whose lease ran out, cancels jobs that waited longer than
    /// <see cref="GpuRunnerOptions.QueuedLifetime"/>, and refreshes the queued jobs' text. Returns how many leases expired.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var now = Now;
        var expired = await db.GpuJobs.AsNoTracking()
            .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            await ExpireLeaseAsync(db, job, now, ct);
        }

        await ExpireQueuedAsync(db, now, ct);
        await RetryUnfinishedAsync(db, now, ct);
        await RefreshWaitingAsync(db, ct);
        return expired.Count;
    }

    /// <summary>The queued job's line for <paramref name="online"/> able runners, <paramref name="busy"/> of them on another job.</summary>
    internal static string WaitingStage(SplatQuality quality, int online, int busy) =>
        online == 0 ? $"waiting for a 3D runner that can train {CaptureSplatDocuments.QualityName(quality)} (none online)"
        : busy >= online ? $"waiting for a 3D runner ({online} online, busy)"
        : busy == 0 ? $"waiting for a 3D runner ({online} online)"
        : $"waiting for a 3D runner ({online} online, {busy} busy)";

    private async Task ExpireLeaseAsync(BlocwerkDbContext db, GpuJob job, DateTimeOffset now, CancellationToken ct)
    {
        if (job.ClaimedAt is { } claimed && claimed + options.MaxJobDuration <= now)
        {
            // Heartbeats alone never keep a claim past the wall-clock cap; that costs a training attempt.
            var hours = options.MaxJobDuration.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);
            logger.LogWarning("GPU job {JobId}: runner {RunnerId} held it longer than {Hours} h", job.Id, job.ClaimedByRunnerId, hours);
            await ReleaseAsync(db, job, ReleaseKind.Failure, $"the training took longer than {hours} h", ct);
            return;
        }

        logger.LogWarning(
            "GPU job {JobId}: the lease of runner {RunnerId} expired (lost {Lost} of {Max})",
            job.Id, job.ClaimedByRunnerId, job.LostLeaseCount + 1, options.MaxLostLeases);
        await ReleaseAsync(db, job, ReleaseKind.LostLease, "the 3D runner stopped responding (its lease expired)", ct);
    }

    /// <summary>
    /// Jobs nobody claimed within <see cref="GpuRunnerOptions.QueuedLifetime"/>: cancelled and their files (the bundle is
    /// a copy of the capture's photos) deleted, so no runner receives them months later.
    /// </summary>
    private async Task ExpireQueuedAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - options.QueuedLifetime;
        var stale = await db.GpuJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued && j.CreatedAt < cutoff).ToListAsync(ct);
        foreach (var job in stale)
        {
            var days = options.QueuedLifetime.TotalDays.ToString("0.#", CultureInfo.InvariantCulture);
            var reason = $"no 3D runner took the photo-real view within {days} days";
            var cancelled = await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == GpuJobStatus.Queued)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, GpuJobStatus.Cancelled)
                        .SetProperty(j => j.Error, reason)
                        .SetProperty(j => j.CompletedAt, now),
                    ct);
            if (cancelled == 0)
            {
                continue;
            }

            logger.LogInformation("GPU job {JobId} of capture {CaptureId} expired: {Reason}", job.Id, job.CaptureId, reason);
            await MarkCaptureWithoutSplatAsync(db, job.CaptureId, reason, ct);
            DeleteFiles(job);
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

    /// <summary>
    /// "waiting for a 3D runner (none online)" / "(1 online, busy)" on every queued job, from the runners' current
    /// heartbeat (a runner that reported its shutdown is offline at once) and whether each one holds a claim right now.
    /// </summary>
    private async Task RefreshWaitingAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var waiting = await db.GpuJobs.AsNoTracking().Where(j => j.Status == GpuJobStatus.Queued).ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return;
        }

        var online = Now - options.OnlineWindow;
        var walls = waiting.Select(j => j.WallId).Distinct().ToList();
        var own = await Assignments(db).Where(rw => walls.Contains(rw.WallId) && rw.Runner.LastSeenAt >= online)
            .Select(rw => new { rw.WallId, rw.RunnerId, rw.Runner.MaxQuality }).ToListAsync(ct);
        var shared = await Approvals(db).Where(a => walls.Contains(a.WallId) && a.Runner.LastSeenAt >= online)
            .Select(a => new { a.WallId, a.RunnerId, a.Runner.MaxQuality }).ToListAsync(ct);
        var busy = (await db.GpuJobs.AsNoTracking()
                .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.ClaimedByRunnerId != null)
                .Select(j => j.ClaimedByRunnerId!.Value).ToListAsync(ct))
            .ToHashSet();
        foreach (var job in waiting)
        {
            var able = own.Concat(shared)
                .Where(o => o.WallId == job.WallId && QualityCap(o.MaxQuality, null) >= job.Quality)
                .Select(o => o.RunnerId).Distinct().ToList();
            var stage = WaitingStage(job.Quality, able.Count, able.Count(busy.Contains));
            if (stage != job.Stage)
            {
                // Conditional: a runner may have claimed it meanwhile (its stage then says so).
                await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == GpuJobStatus.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Stage, stage), ct);
            }
        }
    }
}
