// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// The sweep: expired leases and claims past their run deadline go back to the queue (or fail once the attempts are used
/// up), interactive jobs nobody claimed in time are cancelled, so are interactive jobs left over from before an app restart
/// (their page is gone), and no input envelope stays longer than <see cref="RecognitionQueueOptions.InputRetention"/>.
/// </summary>
public sealed partial class RecognitionQueue
{
    /// <summary>One sweep round; returns how many leases expired.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var now = Now;
        var expired = await db.RecognitionJobs.AsNoTracking()
            .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            logger.LogWarning("Recognition job {JobId}: the lease of runner {RunnerId} expired (claim {Claim})", job.Id, job.ClaimedByRunnerId, job.Attempts);
            await ReleaseAsync(db, job, "the recognition runner stopped responding (its lease expired)", failedBy: null, free: false, ct);
        }

        await EndOverrunsAsync(db, now, ct);
        await CancelOrphanedAsync(db, ct);
        var unclaimed = await db.RecognitionJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued && j.Lane == RecognitionLane.Interactive && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in unclaimed)
        {
            await CancelIfUnclaimedAsync(job, ct);
        }

        await DropOldInputsAsync(db, now, ct);
        return expired.Count;
    }

    /// <summary>Stored names the capture sweep must keep: the inputs of jobs that still have one.</summary>
    public static Task<List<string>> ReferencedFilesAsync(BlocwerkDbContext db, CancellationToken ct) =>
        db.RecognitionJobs.AsNoTracking().Where(j => j.InputPath != null).Select(j => j.InputPath!).ToListAsync(ct);

    /// <summary>
    /// Claims past their run deadline (heartbeats or not) go back to the queue, or fail once the attempts are used up; the
    /// runner is marked, so another one tries first.
    /// </summary>
    private async Task EndOverrunsAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var overrun = await db.RecognitionJobs.AsNoTracking()
            .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.RunDeadlineAt < now)
            .ToListAsync(ct);
        foreach (var job in overrun)
        {
            logger.LogWarning("Recognition job {JobId}: runner {RunnerId} ran longer than {Seconds} s", job.Id, job.ClaimedByRunnerId, job.RunSeconds);
            await ReleaseAsync(db, job, $"the recognition runner took longer than {job.RunSeconds} s", failedBy: job.ClaimedByRunnerId, free: false, ct);
        }
    }

    /// <summary>Interactive jobs queued before this process started: whoever waited for them went with the old process.</summary>
    private async Task CancelOrphanedAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var orphans = await db.RecognitionJobs.AsNoTracking()
            .Where(j => j.Lane == RecognitionLane.Interactive && j.CreatedAt < startedAt
                        && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .ToListAsync(ct);
        foreach (var job in orphans)
        {
            await FinishAsync(db, job, GpuJobStatus.Cancelled, "the app restarted while the job was waiting", null, ct);
        }
    }

    /// <summary>
    /// Queued jobs whose input is older than the retention are cancelled with it (an ended job's input is already gone, a
    /// held one ends by lease or attempts).
    /// </summary>
    private async Task DropOldInputsAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - options.InputRetention;
        var old = await db.RecognitionJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued && j.InputPath != null && j.CreatedAt < cutoff)
            .ToListAsync(ct);
        var hours = options.InputRetention.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);
        foreach (var job in old)
        {
            await FinishAsync(db, job, GpuJobStatus.Cancelled, $"no recognition runner took the job within {hours} h", null, ct);
        }
    }
}
