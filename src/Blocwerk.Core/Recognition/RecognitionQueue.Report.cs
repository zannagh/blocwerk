// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// Job-scoped runner calls: progress (the lease heartbeat), result and failure. Each re-checks that the job is the runner's
/// claim (same claim token) and that the runner may still serve its wall; status changes are conditional updates, so a
/// sweep, a cancel and a heartbeat racing for the same job never overwrite each other.
/// </summary>
public sealed partial class RecognitionQueue
{
    /// <summary>
    /// The runner's claimed job, or why not: <see cref="RunnerJobOutcome.NotYours"/> when it does not hold it,
    /// <see cref="RunnerJobOutcome.Over"/> when the job ended (cancelled, failed, finished), <see cref="RunnerJobOutcome.Gone"/>
    /// when the runner may no longer serve its wall (the job goes back to the queue at no cost).
    /// </summary>
    public async Task<(RunnerJobOutcome Outcome, RecognitionJob? Job)> FindClaimedAsync(GpuRunner runner, Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.RecognitionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.ClaimedByRunnerId != runner.Id
            || (job.ClaimToken is not null && runner.ClaimToken is not null && job.ClaimToken != runner.ClaimToken))
        {
            return (RunnerJobOutcome.NotYours, null);
        }

        if (job.Status is not (GpuJobStatus.Claimed or GpuJobStatus.Running))
        {
            return (RunnerJobOutcome.Over, null);
        }

        if (await MayServeAsync(db, runner, job.WallId, ct))
        {
            return (RunnerJobOutcome.Ok, job);
        }

        await ReleaseAsync(db, job, "the runner may no longer serve this wall", failedBy: null, free: true, ct);
        return (RunnerJobOutcome.Gone, null);
    }

    /// <summary>Records progress and renews the lease by the job's <see cref="RecognitionJob.LeaseSeconds"/>.</summary>
    public async Task<RunnerJobOutcome> ProgressAsync(GpuRunner runner, Guid jobId, RunnerProgress report, CancellationToken ct)
    {
        var (found, job) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok || job is null)
        {
            return found;
        }

        await using var db = dbContextFactory.CreateDbContext();
        return await HeartbeatAsync(db, job, report, ct) ? RunnerJobOutcome.Ok : await LostAsync(runner, jobId, ct);
    }

    /// <summary>
    /// The runner's result (status.json's <c>result</c>): the job succeeds and its waiter wakes.
    /// <see cref="RunnerJobOutcome.Invalid"/> when it is not a JSON object.
    /// </summary>
    public async Task<RunnerJobOutcome> ResultAsync(GpuRunner runner, Guid jobId, string resultJson, CancellationToken ct)
    {
        var (found, job) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok || job is null)
        {
            return found;
        }

        if (!IsJsonObject(resultJson))
        {
            return RunnerJobOutcome.Invalid;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var ended = await FinishAsync(db, job, GpuJobStatus.Succeeded, null, resultJson, ct);
        return ended ? RunnerJobOutcome.Ok : await LostAsync(runner, jobId, ct);
    }

    /// <summary>
    /// The runner gave the job up: a shutdown goes back to the queue at no cost; a retryable failure costs an attempt and
    /// leaves the job to other runners first; anything else fails it now.
    /// </summary>
    public async Task<RunnerJobOutcome> FailAsync(GpuRunner runner, Guid jobId, RunnerFailure failure, CancellationToken ct)
    {
        var (found, job) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok || job is null)
        {
            return found;
        }

        var reason = GpuJobQueue.Clip(failure.Reason, 2048) ?? "the runner gave no reason";
        logger.LogWarning(
            "Runner {RunnerId} failed recognition job {JobId} ({Kind}): {Reason} (retryable {Retryable}, shutdown {Shutdown})",
            runner.Id, job.Id, job.Kind, reason, failure.Retryable, failure.Shutdown);
        await using var db = dbContextFactory.CreateDbContext();
        var changed = failure.Shutdown ? await ReleaseAsync(db, job, reason, failedBy: null, free: true, ct)
            : failure.Retryable ? await ReleaseAsync(db, job, reason, failedBy: runner.Id, free: false, ct)
            : await FinishAsync(db, job, GpuJobStatus.Failed, reason, null, ct);
        return changed ? RunnerJobOutcome.Ok : await LostAsync(runner, jobId, ct);
    }

    /// <summary>
    /// Records progress and renews the lease by the job's <see cref="RecognitionJob.LeaseSeconds"/>, while <paramref name="job"/>
    /// is still held as read (same runner process). Whether it was.
    /// </summary>
    internal async Task<bool> HeartbeatAsync(BlocwerkDbContext db, RecognitionJob job, RunnerProgress report, CancellationToken ct)
    {
        var lease = Now + TimeSpan.FromSeconds(job.LeaseSeconds);
        var progress = report.Fraction is { } f && double.IsFinite(f) ? Math.Clamp(f, 0, 1) : job.Progress;
        var stage = GpuJobQueue.Clip(report.Detail ?? report.Stage, 200) ?? job.Stage;
        var updated = await AsRead(db.RecognitionJobs, job)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Running)
                    .SetProperty(j => j.LeaseExpiresAt, lease)
                    .SetProperty(j => j.Progress, progress)
                    .SetProperty(j => j.Stage, stage),
                ct);
        return updated == 1;
    }

    /// <summary>Why a call that changed nothing failed: the job moved on meanwhile, so never <see cref="RunnerJobOutcome.Ok"/>.</summary>
    private async Task<RunnerJobOutcome> LostAsync(GpuRunner runner, Guid jobId, CancellationToken ct)
    {
        var (outcome, _) = await FindClaimedAsync(runner, jobId, ct);
        return outcome == RunnerJobOutcome.Ok ? RunnerJobOutcome.Gone : outcome;
    }

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
