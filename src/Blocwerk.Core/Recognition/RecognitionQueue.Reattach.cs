// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// A runner that restarted while it held a recognition job claims again and gets that job back, instead of idling until
/// its lease runs out. Only a claim silent for half its lease (several missed heartbeats) is handed back, and the new
/// process takes over the claim token, so an old process still alive is refused at its next call. The claim's run
/// deadline stays as it was, so a runner in a crash loop still ends the claim in time.
/// </summary>
public sealed partial class RecognitionQueue
{
    /// <summary>The job <paramref name="runner"/> holds, renewed for it, when it has been silent long enough; else null.</summary>
    private async Task<RecognitionJob?> ReattachAsync(BlocwerkDbContext db, GpuRunner runner, CancellationToken ct)
    {
        var now = Now;
        var job = await db.RecognitionJobs.AsNoTracking().FirstOrDefaultAsync(
            j => j.ClaimedByRunnerId == runner.Id && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running), ct);
        if (job?.LeaseExpiresAt is not { } lease || lease - TimeSpan.FromSeconds(job.LeaseSeconds / 2.0) > now
            || job.RunDeadlineAt <= now)
        {
            return null;
        }

        if (!await MayServeAsync(db, runner, job.WallId, ct))
        {
            await ReleaseAsync(db, job, "the runner may no longer serve this wall", failedBy: null, free: true, ct);
            return null;
        }

        var renewed = await db.RecognitionJobs
            .Where(j => j.Id == job.Id && j.Status == job.Status && j.ClaimedByRunnerId == runner.Id && j.LeaseExpiresAt == lease)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.LeaseExpiresAt, now + TimeSpan.FromSeconds(job.LeaseSeconds))
                    .SetProperty(j => j.ClaimToken, GpuJobQueue.Clip(runner.ClaimToken, 64))
                    .SetProperty(j => j.Progress, 0)
                    .SetProperty(j => j.Stage, "is downloading the photos (the runner restarted)"),
                ct);
        if (renewed == 0)
        {
            return null;
        }

        logger.LogInformation("Runner {RunnerId} ({Name}) re-attached to recognition job {JobId} it still held", runner.Id, runner.Name, job.Id);
        return await db.RecognitionJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id, ct);
    }

    /// <summary>Whether the runner may (still) serve a job of this wall: the instance runner always, any other by the GPU queue's rule.</summary>
    private async Task<bool> MayServeAsync(BlocwerkDbContext db, GpuRunner runner, Guid wallId, CancellationToken ct) =>
        runner.Scope == RunnerScope.Instance || await gpuQueue.MayServeAsync(db, runner.Id, wallId, ct);
}
