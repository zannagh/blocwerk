// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Releasing a claimed job: back to the queue within the budget its <see cref="ReleaseKind"/> costs, or failed for good.
/// A job that failed for good keeps its last holder, so that runner learns the job is over (410 <c>over</c>) and drops
/// its checkpoints; a requeued one has no holder (404 to the old one, which keeps them for a later claim).
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>Why a claimed job goes back (or ends); decides which budget it costs.</summary>
    internal enum ReleaseKind
    {
        /// <summary>The runner was revoked or lost its eligibility: requeued, costs nothing.</summary>
        Free,

        /// <summary>
        /// The job is paused with its progress kept (a shutdown whose checkpoint got further than any before): requeued,
        /// costs nothing and counts nowhere. The seam for a deliberate pause of a running job.
        /// </summary>
        Pause,

        /// <summary>The runner shut down: free up to <see cref="GpuRunnerOptions.MaxFreeShutdowns"/>, then a failure.</summary>
        Shutdown,

        /// <summary>
        /// A retryable training failure: costs one of <see cref="GpuRunnerOptions.MaxAttempts"/>, and the runner is
        /// remembered (<see cref="GpuJobFailedRunners"/>) so another one gets the next try.
        /// </summary>
        Failure,

        /// <summary>The runner vanished (lease expired): costs one of <see cref="GpuRunnerOptions.MaxLostLeases"/>.</summary>
        LostLease,

        /// <summary>A failure no retry can fix: the job fails now.</summary>
        Fatal,
    }

    /// <summary>
    /// Back to the queue (within the budget <paramref name="kind"/> costs) or failed for good. Conditional on the job
    /// still being in the state and hands <paramref name="job"/> was read in; false when something else moved it first.
    /// On success <paramref name="job"/> carries the new status (<see cref="GpuJobStatus.Queued"/>: it gets another try).
    /// </summary>
    internal async Task<bool> ReleaseAsync(BlocwerkDbContext db, GpuJob job, ReleaseKind kind, string reason, CancellationToken ct)
    {
        var (from, holder) = (job.Status, job.ClaimedByRunnerId);
        var retry = ApplyBudget(job, kind);
        var error = Clip(reason, 2048);
        DateTimeOffset? completed = retry ? null : Now;
        var status = retry ? GpuJobStatus.Queued : GpuJobStatus.Failed;
        var progress = retry ? 0 : job.Progress;
        var stage = retry ? "waiting for a 3D runner (retrying)" : job.Stage;
        var failedRunners = kind == ReleaseKind.Failure && holder is { } h
            ? GpuJobFailedRunners.With(job.FailedRunnerIdsJson, h)
            : job.FailedRunnerIdsJson;
        var changed = await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == from && j.ClaimedByRunnerId == holder)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, status)
                    .SetProperty(j => j.ClaimedByRunnerId, j => retry ? null : j.ClaimedByRunnerId)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.Error, error)
                    .SetProperty(j => j.FailureCount, job.FailureCount)
                    .SetProperty(j => j.LostLeaseCount, job.LostLeaseCount)
                    .SetProperty(j => j.ShutdownCount, job.ShutdownCount)
                    .SetProperty(j => j.CheckpointStep, job.CheckpointStep)
                    .SetProperty(j => j.FailedRunnerIdsJson, failedRunners)
                    .SetProperty(j => j.Progress, progress)
                    .SetProperty(j => j.Stage, stage)
                    .SetProperty(j => j.CompletedAt, completed)
                    .SetProperty(j => j.PreviewPath, j => retry ? j.PreviewPath : null),
                ct);
        if (changed == 0)
        {
            return false;
        }

        job.Status = status;
        if (retry)
        {
            signal.Pulse();
            return true;
        }

        // A preview still pending went with the failure (same update); an installed one stays as the job's leftover.
        await MarkCaptureWithoutSplatAsync(db, job, reason, ct);
        DeleteSpent(await db.GpuJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id, ct), job.PreviewPath);
        return true;
    }

    /// <summary>
    /// What a shutdown hand-back costs: nothing when the runner's checkpoint got further than any shutdown's before (the
    /// next claim resumes there, so the job progresses; the step only grows, so a runner cannot loop on it), else it is
    /// a <see cref="ReleaseKind.Shutdown"/>. Records the new checkpoint step on <paramref name="job"/>.
    /// </summary>
    internal static ReleaseKind ShutdownKind(GpuJob job, int? checkpointStep)
    {
        if (checkpointStep is not { } step || step <= 0 || step <= (job.CheckpointStep ?? 0))
        {
            return ReleaseKind.Shutdown;
        }

        job.CheckpointStep = step;
        return ReleaseKind.Pause;
    }

    /// <summary>Counts what <paramref name="kind"/> costs on <paramref name="job"/>; whether the job gets another try.</summary>
    private bool ApplyBudget(GpuJob job, ReleaseKind kind)
    {
        if (kind == ReleaseKind.Shutdown && ++job.ShutdownCount > options.MaxFreeShutdowns)
        {
            kind = ReleaseKind.Failure;
        }

        return kind switch
        {
            ReleaseKind.Free or ReleaseKind.Pause or ReleaseKind.Shutdown => true,
            ReleaseKind.Failure => ++job.FailureCount < options.MaxAttempts,
            ReleaseKind.LostLease => ++job.LostLeaseCount < options.MaxLostLeases,
            _ => false,
        };
    }

    /// <summary>
    /// A runner that said it is shutting down is offline now, not for another <see cref="GpuRunnerOptions.OnlineWindow"/>
    /// ("1 online, busy" on the job it just handed back). Its next call marks it online again.
    /// </summary>
    private async Task MarkStoppedAsync(BlocwerkDbContext db, Guid runnerId, CancellationToken ct)
    {
        var offline = Now - options.OnlineWindow - TimeSpan.FromSeconds(1);
        await db.GpuRunners.Where(r => r.Id == runnerId && r.LastSeenAt > offline)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastSeenAt, offline), ct);
    }
}
