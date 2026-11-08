// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// The sweep: expired leases go back to the queue (or fail once the attempts are used up), interactive jobs nobody claimed
/// in time are cancelled, so are interactive jobs left over from before an app restart (their page is gone), and no input
/// envelope stays longer than <see cref="RecognitionQueueOptions.InputRetention"/>.
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
    /// Inputs older than the retention: a job that is not held by a runner right now is cancelled with it (a queued one
    /// waited too long); an ended job whose delete failed loses the leftover.
    /// </summary>
    private async Task DropOldInputsAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - options.InputRetention;
        var old = await db.RecognitionJobs.AsNoTracking()
            .Where(j => j.InputPath != null && j.CreatedAt < cutoff && j.Status != GpuJobStatus.Claimed && j.Status != GpuJobStatus.Running)
            .ToListAsync(ct);
        var hours = options.InputRetention.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);
        foreach (var job in old)
        {
            if (job.Status == GpuJobStatus.Queued)
            {
                await FinishAsync(db, job, GpuJobStatus.Cancelled, $"no recognition runner took the job within {hours} h", null, ct);
                continue;
            }

            var dropped = await db.RecognitionJobs.Where(j => j.Id == job.Id && j.InputPath == job.InputPath)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.InputPath, (string?)null), ct);
            if (dropped == 1)
            {
                DeleteInput(job);
            }
        }
    }
}
