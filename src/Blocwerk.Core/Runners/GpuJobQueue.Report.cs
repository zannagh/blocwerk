// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>Job-scoped runner calls: the bundle, progress (the lease heartbeat) and failure.</summary>
public sealed partial class GpuJobQueue
{
    /// <summary>Why a claimed job goes back (or ends); decides which budget it costs.</summary>
    internal enum ReleaseKind
    {
        /// <summary>The runner shut down or was revoked: requeued, costs nothing.</summary>
        Free,

        /// <summary>A retryable training failure: costs one of <see cref="GpuRunnerOptions.MaxAttempts"/>.</summary>
        Failure,

        /// <summary>The runner vanished (lease expired): costs one of <see cref="GpuRunnerOptions.MaxLostLeases"/>.</summary>
        LostLease,

        /// <summary>A failure no retry can fix: the job fails now.</summary>
        Fatal,
    }

    /// <summary>
    /// The runner's claimed job, or why not: <see cref="RunnerJobOutcome.NotYours"/> when it never
    /// held it, <see cref="RunnerJobOutcome.Gone"/> when it held it but no longer does.
    /// </summary>
    public async Task<(RunnerJobOutcome Outcome, GpuJob? Job)> FindClaimedAsync(GpuRunner runner, Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.ClaimedByRunnerId != runner.Id)
        {
            return (RunnerJobOutcome.NotYours, null);
        }

        return job.Status is GpuJobStatus.Claimed or GpuJobStatus.Running
            ? (RunnerJobOutcome.Ok, job)
            : (RunnerJobOutcome.Gone, null);
    }

    /// <summary>A stored bundle's physical path, for streaming it to the runner that claimed the job.</summary>
    public string? BundlePath(GpuJob job) => files.ResolvePhysicalPath(job.BundlePath);

    /// <summary>Records progress and extends the lease. <see cref="RunnerJobOutcome.Gone"/> tells the runner to stop.</summary>
    public async Task<RunnerJobOutcome> ProgressAsync(GpuRunner runner, Guid jobId, RunnerProgress report, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.ClaimedByRunnerId == runner.Id, ct);
        if (job is null)
        {
            return RunnerJobOutcome.NotYours;
        }

        if (job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running))
        {
            return RunnerJobOutcome.Gone;
        }

        job.Status = GpuJobStatus.Running;
        job.LeaseExpiresAt = Now + options.Lease;
        job.Progress = double.IsFinite(report.Fraction ?? 0) ? Math.Clamp(report.Fraction ?? job.Progress, 0, 1) : job.Progress;
        job.Stage = Clip(Describe(report), 200);
        await db.SaveChangesAsync(ct);
        return RunnerJobOutcome.Ok;
    }

    /// <summary>
    /// The runner gave up. A shutdown requeues the job for free; a retryable failure requeues it while training
    /// attempts are left; anything else (or no attempts left) fails the job.
    /// </summary>
    public async Task<RunnerJobOutcome> FailAsync(GpuRunner runner, Guid jobId, RunnerFailure failure, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.ClaimedByRunnerId == runner.Id, ct);
        if (job is null)
        {
            return RunnerJobOutcome.NotYours;
        }

        if (job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running))
        {
            return RunnerJobOutcome.Gone;
        }

        var reason = Clip(failure.Reason, 1000) ?? "the runner reported a failure";
        var kind = failure.Shutdown ? ReleaseKind.Free : failure.Retryable ? ReleaseKind.Failure : ReleaseKind.Fatal;
        logger.LogWarning(
            "Runner {RunnerId} ({Name}) gave GPU job {JobId} back ({Kind}; failures {Failures}): {Reason}",
            runner.Id, runner.Name, job.Id, kind, job.FailureCount, reason);
        var text = failure.Shutdown ? "the 3D runner shut down" : $"training on the 3D runner failed: {reason}";
        await ReleaseAsync(db, job, kind, text, ct);
        return RunnerJobOutcome.Ok;
    }

    /// <summary>Back to the queue (within the budget <paramref name="kind"/> costs) or failed for good; saves.</summary>
    internal async Task ReleaseAsync(BlocwerkDbContext db, GpuJob job, ReleaseKind kind, string reason, CancellationToken ct)
    {
        job.ClaimedByRunnerId = null;
        job.LeaseExpiresAt = null;
        job.Error = Clip(reason, 2048);
        var retry = kind switch
        {
            ReleaseKind.Free => true,
            ReleaseKind.Failure => ++job.FailureCount < options.MaxAttempts,
            ReleaseKind.LostLease => ++job.LostLeaseCount < options.MaxLostLeases,
            _ => false,
        };
        if (retry)
        {
            job.Status = GpuJobStatus.Queued;
            job.Progress = 0;
            job.Stage = "waiting for a 3D runner (retrying)";
            await db.SaveChangesAsync(ct);
            signal.Pulse();
            return;
        }

        job.Status = GpuJobStatus.Failed;
        job.CompletedAt = Now;
        await db.SaveChangesAsync(ct);
        await MarkCaptureWithoutSplatAsync(db, job.CaptureId, reason, ct);
        DeleteFiles(job);
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
