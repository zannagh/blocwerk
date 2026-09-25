// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Job-scoped runner calls: the bundle, progress (the lease heartbeat) and failure. Every one re-checks that the job is
/// the runner's claim AND that the runner may still train its wall; status changes are conditional updates, so a
/// sweep, an upload and a heartbeat racing for the same job never overwrite each other.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>Why a claimed job goes back (or ends); decides which budget it costs.</summary>
    internal enum ReleaseKind
    {
        /// <summary>The runner was revoked or lost its eligibility: requeued, costs nothing.</summary>
        Free,

        /// <summary>The runner shut down: free up to <see cref="GpuRunnerOptions.MaxFreeShutdowns"/>, then a failure.</summary>
        Shutdown,

        /// <summary>A retryable training failure: costs one of <see cref="GpuRunnerOptions.MaxAttempts"/>.</summary>
        Failure,

        /// <summary>The runner vanished (lease expired): costs one of <see cref="GpuRunnerOptions.MaxLostLeases"/>.</summary>
        LostLease,

        /// <summary>A failure no retry can fix: the job fails now.</summary>
        Fatal,
    }

    /// <summary>
    /// The runner's claimed job, or why not: <see cref="RunnerJobOutcome.NotYours"/> when it never held it,
    /// <see cref="RunnerJobOutcome.Gone"/> when it held it but no longer does, or may no longer train its wall (then
    /// the claim goes back to the queue).
    /// </summary>
    public async Task<(RunnerJobOutcome Outcome, GpuJob? Job)> FindClaimedAsync(GpuRunner runner, Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.ClaimedByRunnerId != runner.Id)
        {
            return (RunnerJobOutcome.NotYours, null);
        }

        if (job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running) || !await StillEligibleAsync(db, runner, job, ct))
        {
            return (RunnerJobOutcome.Gone, null);
        }

        return (RunnerJobOutcome.Ok, job);
    }

    /// <summary>A stored bundle's physical path, for streaming it to the runner that claimed the job.</summary>
    public string? BundlePath(GpuJob job) => files.ResolvePhysicalPath(job.BundlePath);

    /// <summary>
    /// Records progress and extends the lease, never past <see cref="GpuRunnerOptions.MaxJobDuration"/> after the claim.
    /// <see cref="RunnerJobOutcome.Gone"/> tells the runner to stop.
    /// </summary>
    public async Task<RunnerJobOutcome> ProgressAsync(GpuRunner runner, Guid jobId, RunnerProgress report, CancellationToken ct)
    {
        var (found, job) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok || job is null)
        {
            return found;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var now = Now;
        var deadline = (job.ClaimedAt ?? now) + options.MaxJobDuration;
        if (now >= deadline)
        {
            var hours = options.MaxJobDuration.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);
            await ReleaseAsync(db, job, ReleaseKind.Failure, $"the training took longer than {hours} h", ct);
            return RunnerJobOutcome.Gone;
        }

        var lease = now + options.Lease < deadline ? now + options.Lease : deadline;
        var progress = report.Fraction is { } f && double.IsFinite(f) ? Math.Clamp(f, 0, 1) : job.Progress;
        var stage = Clip(Describe(report), 200);
        var updated = await db.GpuJobs
            .Where(j => j.Id == jobId && j.ClaimedByRunnerId == runner.Id
                        && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Running)
                    .SetProperty(j => j.LeaseExpiresAt, lease)
                    .SetProperty(j => j.Progress, progress)
                    .SetProperty(j => j.Stage, stage),
                ct);
        return updated == 0 ? RunnerJobOutcome.Gone : RunnerJobOutcome.Ok;
    }

    /// <summary>
    /// The runner gave up. A shutdown requeues the job (for free a few times); a retryable failure requeues it while
    /// training attempts are left; anything else (or no attempts left) fails the job.
    /// </summary>
    public async Task<RunnerJobOutcome> FailAsync(GpuRunner runner, Guid jobId, RunnerFailure failure, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.ClaimedByRunnerId == runner.Id, ct);
        if (job is null)
        {
            return RunnerJobOutcome.NotYours;
        }

        if (job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running))
        {
            return RunnerJobOutcome.Gone;
        }

        var reason = Clip(failure.Reason, 1000) ?? "the runner reported a failure";
        var kind = failure.Shutdown ? ReleaseKind.Shutdown : failure.Retryable ? ReleaseKind.Failure : ReleaseKind.Fatal;
        logger.LogWarning(
            "Runner {RunnerId} ({Name}) gave GPU job {JobId} back ({Kind}; failures {Failures}, shutdowns {Shutdowns}): {Reason}",
            runner.Id, runner.Name, job.Id, kind, job.FailureCount, job.ShutdownCount, reason);
        var text = failure.Shutdown ? "the 3D runner shut down" : $"training on the 3D runner failed: {reason}";
        return await ReleaseAsync(db, job, kind, text, ct) ? RunnerJobOutcome.Ok : RunnerJobOutcome.Gone;
    }

    /// <summary>
    /// Back to the queue (within the budget <paramref name="kind"/> costs) or failed for good. Conditional on the job
    /// still being in the state and hands <paramref name="job"/> was read in; false when something else moved it first.
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
        var changed = await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == from && j.ClaimedByRunnerId == holder)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, status)
                    .SetProperty(j => j.ClaimedByRunnerId, (Guid?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.Error, error)
                    .SetProperty(j => j.FailureCount, job.FailureCount)
                    .SetProperty(j => j.LostLeaseCount, job.LostLeaseCount)
                    .SetProperty(j => j.ShutdownCount, job.ShutdownCount)
                    .SetProperty(j => j.Progress, progress)
                    .SetProperty(j => j.Stage, stage)
                    .SetProperty(j => j.CompletedAt, completed),
                ct);
        if (changed == 0)
        {
            return false;
        }

        if (retry)
        {
            signal.Pulse();
            return true;
        }

        await MarkCaptureWithoutSplatAsync(db, job.CaptureId, reason, ct);
        DeleteFiles(job);
        return true;
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
            ReleaseKind.Free or ReleaseKind.Shutdown => true,
            ReleaseKind.Failure => ++job.FailureCount < options.MaxAttempts,
            ReleaseKind.LostLease => ++job.LostLeaseCount < options.MaxLostLeases,
            _ => false,
        };
    }

    private static string Describe(RunnerProgress report)
    {
        var stage = report.Stage switch
        {
            "download" => "is downloading the photos",
            "upload" => "is uploading the trained view",
            _ => "is training",
        };
        if (report is { Step: { } step, TotalSteps: > 0 })
        {
            stage += string.Create(CultureInfo.InvariantCulture, $" (step {step}/{report.TotalSteps})");
        }

        // The runner's detail often repeats the step counter; keep only what it adds.
        var detail = report.Detail?.Trim();
        return detail is { Length: > 0 } && !detail.StartsWith("step ", StringComparison.OrdinalIgnoreCase)
            ? $"{stage}; {Clip(detail, 80)}"
            : stage;
    }
}
