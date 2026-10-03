// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// A runner that restarted (crash, OOM kill, <c>docker restart</c>) while it held a job claims again and gets that job
/// back, instead of idling until the lease runs out. The job id stays the same, so the runner resumes from its checkpoint
/// (keyed by job id and bundle). Only a claim that has been silent for <see cref="ReattachSilence"/> is handed back, and
/// the restarted process takes over the claim token (<see cref="GpuJob.ClaimToken"/>): should the old process still be
/// alive (a second process with the same key, a network blip), its next job call is refused and it stops. The first
/// <see cref="GpuRunnerOptions.MaxReattaches"/> re-attaches of a job are free; each further one costs a lost lease, so a
/// runner in a crash loop ends the job like a vanished one would, only sooner.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>How long a held job must have been silent before its runner may take it over again (heartbeats: at most every 20 s).</summary>
    internal static readonly TimeSpan ReattachSilence = TimeSpan.FromSeconds(45);

    /// <summary>The job <paramref name="runner"/> holds, renewed for it, when it has been silent long enough; else null.</summary>
    private async Task<GpuJob?> ReattachAsync(BlocwerkDbContext db, GpuRunner runner, CancellationToken ct)
    {
        var now = Now;
        var job = await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(
            j => j.ClaimedByRunnerId == runner.Id && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running), ct);
        if (job is null || (job.HeartbeatAt ?? job.ClaimedAt) is not { } heard || now - heard < ReattachSilence)
        {
            return null;
        }

        // Past the wall-clock cap the sweep fails the attempt; a runner that lost the wall gets something else next time.
        var deadline = Deadline(job.ClaimedAt ?? now, uploading: false);
        if (now >= deadline || !await StillEligibleAsync(db, runner, job, ct))
        {
            return null;
        }

        var charged = job.ReattachCount >= options.MaxReattaches;
        if (charged && job.LostLeaseCount + 1 >= options.MaxLostLeases)
        {
            logger.LogWarning("Runner {RunnerId} ({Name}) keeps restarting on GPU job {JobId}; the job fails", runner.Id, runner.Name, job.Id);
            await ReleaseAsync(db, job, ReleaseKind.LostLease, "the 3D runner kept restarting", ct);
            return null;
        }

        return await RenewForAsync(db, runner, job, charged, now, deadline, ct);
    }

    private async Task<GpuJob?> RenewForAsync(
        BlocwerkDbContext db, GpuRunner runner, GpuJob job, bool charged, DateTimeOffset now, DateTimeOffset deadline, CancellationToken ct)
    {
        var lease = now + options.Lease < deadline ? now + options.Lease : deadline;
        var heartbeat = job.HeartbeatAt;
        var lost = charged ? 1 : 0;
        var renewed = await db.GpuJobs
            .Where(j => j.Id == job.Id && j.ClaimedByRunnerId == runner.Id && j.Status == job.Status && j.HeartbeatAt == heartbeat)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.LeaseExpiresAt, lease)
                    .SetProperty(j => j.HeartbeatAt, now)
                    .SetProperty(j => j.ClaimToken, Clip(runner.ClaimToken, 64))
                    .SetProperty(j => j.ReattachCount, j => j.ReattachCount + 1)
                    .SetProperty(j => j.LostLeaseCount, j => j.LostLeaseCount + lost)
                    .SetProperty(j => j.Progress, 0)
                    .SetProperty(j => j.Stage, "is downloading the photos (the runner restarted)"),
                ct);
        if (renewed == 0)
        {
            return null;
        }

        logger.LogInformation(
            "Runner {RunnerId} ({Name}) re-attached to GPU job {JobId} it still held (re-attach {Count}{Charged})",
            runner.Id, runner.Name, job.Id, job.ReattachCount + 1, charged ? ", costs a lost lease" : string.Empty);
        return await db.GpuJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id, ct);
    }
}
