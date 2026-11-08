// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// How a job ends or goes back: every change is conditional on the job being as it was read (<see cref="AsRead"/>). An ended job keeps
/// its last holder, so that runner's next call hears <c>410 over</c>; its input envelope is deleted and its waiter woken.
/// </summary>
public sealed partial class RecognitionQueue
{
    /// <summary>Cancels a job that has not ended; its runner stops at its next heartbeat. False when it already ended.</summary>
    public async Task<bool> CancelAsync(Guid jobId, string reason, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        for (var tries = 0; tries < 3; tries++)
        {
            var job = await db.RecognitionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null || IsOver(job.Status))
            {
                return false;
            }

            if (await FinishAsync(db, job, GpuJobStatus.Cancelled, reason, null, ct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ends the job with <paramref name="status"/>, deletes its input and wakes its waiter. False when it moved first.</summary>
    internal async Task<bool> FinishAsync(
        BlocwerkDbContext db, RecognitionJob job, GpuJobStatus status, string? error, string? resultJson, CancellationToken ct)
    {
        var now = Now;
        var changed = await AsRead(db.RecognitionJobs, job)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, status)
                    .SetProperty(j => j.Error, GpuJobQueue.Clip(error, 2048))
                    .SetProperty(j => j.ResultJson, resultJson)
                    .SetProperty(j => j.Progress, status == GpuJobStatus.Succeeded ? 1 : job.Progress)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.InputPath, (string?)null)
                    .SetProperty(j => j.CompletedAt, now),
                ct);
        if (changed == 0)
        {
            return false;
        }

        logger.LogInformation("Recognition job {JobId} ({Kind}) ended {Status}: {Error}", job.Id, job.Kind, status, error);
        DeleteInput(job);
        waiters.Complete(job.Id);
        return true;
    }

    /// <summary>
    /// <paramref name="job"/>'s row while it is as read: a held job still held by the same runner process (claimed or
    /// running: a heartbeat in between does not matter), any other job still in the status it was read in.
    /// </summary>
    internal static IQueryable<RecognitionJob> AsRead(IQueryable<RecognitionJob> jobs, RecognitionJob job)
    {
        var (id, from, holder, token) = (job.Id, job.Status, job.ClaimedByRunnerId, job.ClaimToken);
        return from is GpuJobStatus.Claimed or GpuJobStatus.Running
            ? jobs.Where(j => j.Id == id && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running)
                              && j.ClaimedByRunnerId == holder && j.ClaimToken == token)
            : jobs.Where(j => j.Id == id && j.Status == from && j.ClaimedByRunnerId == holder);
    }

    /// <summary>An interactive job past its claim deadline is cancelled (no runner took it). Whether it was.</summary>
    private async Task<bool> CancelIfUnclaimedAsync(RecognitionJob job, CancellationToken ct)
    {
        if (job is not { Status: GpuJobStatus.Queued, Lane: RecognitionLane.Interactive, LeaseExpiresAt: { } deadline } || deadline > Now)
        {
            return false;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var seconds = (int)options.InteractiveClaimWait.TotalSeconds;
        return await FinishAsync(db, job, GpuJobStatus.Cancelled, $"no recognition runner took the job within {seconds} s", null, ct);
    }

    /// <summary>
    /// Back to the queue, or failed once the lane's attempts are used up. <paramref name="free"/> (a shutdown, a lost
    /// eligibility) gives the claim's attempt back; <paramref name="failedBy"/> marks a runner that failed it.
    /// </summary>
    private async Task<bool> ReleaseAsync(
        BlocwerkDbContext db, RecognitionJob job, string reason, Guid? failedBy, bool free, CancellationToken ct)
    {
        if (!free && job.Attempts >= options.AttemptsOf(job.Lane))
        {
            return await FinishAsync(db, job, GpuJobStatus.Failed, reason, null, ct);
        }

        var attempts = free ? Math.Max(0, job.Attempts - 1) : job.Attempts;
        var failedRunners = failedBy is { } id ? GpuJobFailedRunners.With(job.FailedRunnerIdsJson, id) : job.FailedRunnerIdsJson;
        var deadline = ClaimDeadline(job.Lane, Now);
        var changed = await AsRead(db.RecognitionJobs, job)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Queued)
                    .SetProperty(j => j.ClaimedByRunnerId, (Guid?)null)
                    .SetProperty(j => j.ClaimToken, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, deadline)
                    .SetProperty(j => j.Attempts, attempts)
                    .SetProperty(j => j.FailedRunnerIdsJson, failedRunners)
                    .SetProperty(j => j.Error, GpuJobQueue.Clip(reason, 2048))
                    .SetProperty(j => j.Progress, 0)
                    .SetProperty(j => j.Stage, $"{WaitingStage} (retrying)"),
                ct);
        if (changed == 0)
        {
            return false;
        }

        signal.Pulse();
        return true;
    }

    private void DeleteInput(RecognitionJob job)
    {
        try
        {
            files.Delete(job.InputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the input of recognition job {JobId}; the capture sweep removes it as an orphan", job.Id);
        }
    }
}
