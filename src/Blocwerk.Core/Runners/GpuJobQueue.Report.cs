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
    /// <summary>What the stage text of a job says while its runner uploads the trained view.</summary>
    internal const string UploadingStage = "is uploading the trained view";

    /// <summary>
    /// The runner's claimed job, or why not: <see cref="RunnerJobOutcome.NotYours"/> when it never held it (or it went
    /// back to the queue), <see cref="RunnerJobOutcome.Over"/> when it held it and the job is over for good (cancelled,
    /// failed, finished), <see cref="RunnerJobOutcome.Gone"/> when it may no longer train its wall (then the claim goes
    /// back to the queue).
    /// </summary>
    public async Task<(RunnerJobOutcome Outcome, GpuJob? Job)> FindClaimedAsync(GpuRunner runner, Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.ClaimedByRunnerId != runner.Id || !HoldsToken(job, runner))
        {
            return (RunnerJobOutcome.NotYours, null);
        }

        if (job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running))
        {
            return (RunnerJobOutcome.Over, null);
        }

        return await StillEligibleAsync(db, runner, job, ct) ? (RunnerJobOutcome.Ok, job) : (RunnerJobOutcome.Gone, null);
    }

    /// <summary>
    /// The claimed job's bundle file, for streaming it to its runner. A bundle that is missing (or not the size it was
    /// stored at) cannot be trained by any runner: the job fails at once with that reason (<see cref="RunnerJobOutcome.Over"/>),
    /// instead of every runner giving up on it until its lost-lease budget runs out.
    /// </summary>
    public async Task<(RunnerJobOutcome Outcome, string? Path)> BundleForRunnerAsync(GpuRunner runner, Guid jobId, CancellationToken ct)
    {
        var (found, job) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok || job is null)
        {
            return (found, null);
        }

        var path = files.ResolvePhysicalPath(job.BundlePath);
        if (path is not null && File.Exists(path) && new FileInfo(path).Length == job.BundleBytes)
        {
            return (RunnerJobOutcome.Ok, path);
        }

        logger.LogError("GPU job {JobId}: its training bundle {Bundle} is missing or damaged; the job fails", job.Id, job.BundlePath);
        await using var db = dbContextFactory.CreateDbContext();
        var released = await ReleaseAsync(db, job, ReleaseKind.Fatal, "the training bundle is missing or damaged on the server", ct);
        return (released ? RunnerJobOutcome.Over : RunnerJobOutcome.Gone, null);
    }

    /// <summary>
    /// Records progress and extends the lease, never past <see cref="GpuRunnerOptions.MaxJobDuration"/> after the claim
    /// (plus <see cref="GpuRunnerOptions.MaxUploadDuration"/> while the trained view uploads, so a training that ended
    /// near the cap is not cut off mid-upload). <see cref="RunnerJobOutcome.Gone"/> or <see cref="RunnerJobOutcome.Over"/>
    /// tells the runner to stop.
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
        var deadline = Deadline(job.ClaimedAt ?? now, report.Stage == "upload");
        if (now >= deadline)
        {
            var released = await ReleaseAsync(db, job, ReleaseKind.Failure, TooLongReason(), ct);
            return released && job.Status != GpuJobStatus.Queued ? RunnerJobOutcome.Over : RunnerJobOutcome.Gone;
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
                    .SetProperty(j => j.Stage, stage)
                    .SetProperty(j => j.HeartbeatAt, now)
                    .SetProperty(j => j.Error, (string?)null),
                ct);
        return updated == 0 ? RunnerJobOutcome.Gone : RunnerJobOutcome.Ok;
    }

    /// <summary>
    /// The runner gave up. A pause requeues the job for free (the runner stays online, paused); a shutdown requeues it
    /// (for free while its checkpoint advances, else a few times); a retryable failure requeues it while training attempts
    /// are left (for another runner first); anything else (or no attempts left) fails the job.
    /// </summary>
    public async Task<RunnerJobOutcome> FailAsync(GpuRunner runner, Guid jobId, RunnerFailure failure, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.ClaimedByRunnerId == runner.Id, ct);
        if (job is null || !HoldsToken(job, runner))
        {
            return RunnerJobOutcome.NotYours;
        }

        if (job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running))
        {
            return RunnerJobOutcome.Over;
        }

        var reason = Clip(failure.Reason, 1000) ?? "the runner reported a failure";
        var kind = failure.Shutdown ? ShutdownKind(job, failure.CheckpointStep)
            : failure.Unreachable ? ReleaseKind.LostLease
            : failure.Retryable ? ReleaseKind.Failure : ReleaseKind.Fatal;
        var paused = failure.Shutdown && failure.Pause;
        if (paused && (kind == ReleaseKind.Pause || job.PauseCount < options.MaxStalledPauses))
        {
            // The owner paused the runner: free (capped by MaxPauses), and while its checkpoint did not advance only
            // while the job has had fewer than MaxStalledPauses pauses; past that it costs a shutdown.
            kind = ReleaseKind.Pause;
        }

        logger.LogWarning(
            "Runner {RunnerId} ({Name}) gave GPU job {JobId} back ({Kind}; failures {Failures}, shutdowns {Shutdowns}): {Reason}",
            runner.Id, runner.Name, job.Id, kind, job.FailureCount, job.ShutdownCount, reason);
        var text = paused ? "the 3D runner was paused"
            : failure.Shutdown ? "the 3D runner shut down"
            : failure.Unreachable ? $"the 3D runner gave the job back: {reason}"
            : $"training on the 3D runner failed: {reason}";
        var released = await ReleaseAsync(db, job, kind, text, ct);
        if (failure.Shutdown && !paused)
        {
            await MarkStoppedAsync(db, runner.Id, ct);
        }

        return released ? RunnerJobOutcome.Ok : RunnerJobOutcome.Gone;
    }

    /// <summary>
    /// Whether the calling process holds the job's claim token: always for a runner (or a job) without one, so runners that
    /// send no token keep working; a second process of the same key with another token does not.
    /// </summary>
    internal static bool HoldsToken(GpuJob job, GpuRunner runner) =>
        job.ClaimToken is null || runner.ClaimToken is null || job.ClaimToken == runner.ClaimToken;

    /// <summary>
    /// Until when a claim made at <paramref name="claimedAt"/> may run: <see cref="GpuRunnerOptions.MaxJobDuration"/>, plus
    /// <see cref="GpuRunnerOptions.MaxUploadDuration"/> once the trained view is <paramref name="uploading"/>.
    /// </summary>
    private DateTimeOffset Deadline(DateTimeOffset claimedAt, bool uploading) =>
        claimedAt + options.MaxJobDuration + (uploading ? options.MaxUploadDuration : TimeSpan.Zero);

    private string TooLongReason() =>
        $"the training took longer than {options.MaxJobDuration.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";

    private static string Describe(RunnerProgress report)
    {
        var stage = report.Stage switch
        {
            "download" => "is downloading the photos",
            "upload" => UploadingStage,
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
