// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// The durable hold-recognition queue (runner protocol 1.2): jobs in <see cref="BlocwerkDbContext.RecognitionJobs"/>, their
/// input envelopes in the capture store, claimed by runners under a lease. Interactive jobs come before background ones
/// (<see cref="RecognitionLane"/>), oldest first within a lane. Callers await the end of a job (<see cref="WaitAsync"/>),
/// woken by <see cref="RecognitionWaiters"/>.
/// </summary>
/// <remarks>
/// Single-instance app, like <see cref="GpuJobQueue"/>: claims are serialised in process and guarded by a conditional
/// update, the row is the durable truth, and the sweep (<see cref="RecognitionSweepWorker"/>) requeues what a vanished
/// runner held. Who may serve a wall is <see cref="GpuJobQueue"/>'s rule (its own runners and approved shared ones), and
/// waiting runners are woken by the same <see cref="GpuJobSignal"/>.
/// </remarks>
public sealed partial class RecognitionQueue(
    RootDbContextFactory dbContextFactory,
    ICaptureFileStore files,
    RecognitionQueueOptions options,
    GpuJobQueue gpuQueue,
    GpuJobSignal signal,
    RecognitionWaiters waiters,
    ILogger<RecognitionQueue> logger,
    TimeProvider? clock = null)
{
    /// <summary>The envelope's <c>service</c> for the recognition kinds.</summary>
    public const string Service = "recognition";

    private const string WaitingStage = "waiting for recognition";

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>When this process's queue started: interactive jobs older than it lost their caller with the restart.</summary>
    private readonly DateTimeOffset startedAt = (clock ?? TimeProvider.System).GetUtcNow();

    public RecognitionQueueOptions Options => options;

    internal DateTimeOffset Now => time.GetUtcNow();

    /// <summary>
    /// Writes the input envelope to the capture store, queues the job and wakes waiting runners. An interactive job must be
    /// claimed within <see cref="RecognitionQueueOptions.InteractiveClaimWait"/>.
    /// </summary>
    public async Task<RecognitionJob> EnqueueAsync(RecognitionRequest request, CancellationToken ct)
    {
        if (!RecognitionKinds.IsKnown(request.Kind))
        {
            throw new ArgumentException($"{request.Kind} is not a recognition kind.", nameof(request));
        }

        var (stored, bytes, sha) = await RunnerComputeEnvelope.StoreAsync(files, request.Kind, request.Parts, options.MaxInputBytes, ct, Service);
        var now = Now;
        var job = new RecognitionJob
        {
            Kind = request.Kind, Lane = request.Lane, WallId = request.WallId, InputPath = stored, InputBytes = bytes, InputSha256 = sha,
            RequiredMemoryMb = request.RequiredMemoryMb, LeaseSeconds = (int)options.LeaseOf(request.Lane).TotalSeconds,
            RunSeconds = (int)options.RunOf(request.Kind, request.Lane, request.Parts.Count(p => p.Name == "photos")).TotalSeconds,
            LeaseExpiresAt = ClaimDeadline(request.Lane, now), CreatedAt = now, Stage = WaitingStage,
            CallerKey = GpuJobQueue.Clip(request.CallerKey, 200),
        };
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            db.RecognitionJobs.Add(job);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            files.Delete(stored);
            throw;
        }

        logger.LogInformation(
            "Recognition job {JobId} ({Kind}, {Lane}) queued for wall {WallId} (input {Bytes} bytes, {Memory} MB)",
            job.Id, job.Kind, job.Lane, job.WallId, bytes, job.RequiredMemoryMb);
        signal.Pulse();
        return job;
    }

    /// <summary>The job, or null.</summary>
    public async Task<RecognitionJob?> FindAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.RecognitionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
    }

    /// <summary>When the instance runner (the recognition pod) last called, e.g. to claim; null without one.</summary>
    public async Task<DateTimeOffset?> InstanceRunnerSeenAtAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuRunners.AsNoTracking().Where(r => r.Scope == RunnerScope.Instance).Select(r => r.LastSeenAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>Whether the instance runner called within <see cref="RecognitionQueueOptions.InstanceStaleAfter"/>.</summary>
    public async Task<bool> InstanceRunnerAliveAsync(CancellationToken ct) =>
        await InstanceRunnerSeenAtAsync(ct) is { } seen && Now - seen <= options.InstanceStaleAfter;

    /// <summary>The newest job the caller queued under <paramref name="callerKey"/> (a background job after a restart), or null.</summary>
    public async Task<RecognitionJob?> FindByCallerKeyAsync(string callerKey, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var jobs = await db.RecognitionJobs.AsNoTracking().Where(j => j.CallerKey == callerKey).ToListAsync(ct);
        return jobs.MaxBy(j => j.CreatedAt);
    }

    /// <summary>
    /// Awaits the end of the job (succeeded, failed or cancelled) and returns it; null when it does not exist. Woken by
    /// <see cref="RecognitionWaiters"/>, re-reading every <see cref="RecognitionQueueOptions.WaiterPoll"/> anyway. When
    /// <paramref name="ct"/> fires on an interactive job (the page went away), the job is cancelled, so its runner stops at
    /// its next heartbeat; a background job stays queued (its caller finds it again, e.g. after a restart).
    /// </summary>
    public async Task<RecognitionJob?> WaitAsync(Guid jobId, CancellationToken ct)
    {
        waiters.Watch(jobId);
        try
        {
            while (true)
            {
                var ended = waiters.Next(jobId);
                var job = await FindAsync(jobId, ct);
                if (job is null || IsOver(job.Status))
                {
                    return job;
                }

                if (await CancelIfUnclaimedAsync(job, ct))
                {
                    continue;
                }

                await WakeOrPollAsync(ended, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if ((await FindAsync(jobId, CancellationToken.None))?.Lane == RecognitionLane.Interactive)
            {
                await CancelAsync(jobId, "the caller stopped waiting", CancellationToken.None);
            }

            throw;
        }
        finally
        {
            waiters.Forget(jobId);
        }
    }

    internal static bool IsOver(GpuJobStatus status) =>
        status is GpuJobStatus.Succeeded or GpuJobStatus.Failed or GpuJobStatus.Cancelled;

    private DateTimeOffset? ClaimDeadline(RecognitionLane lane, DateTimeOffset now) =>
        lane == RecognitionLane.Interactive ? now + options.InteractiveClaimWait : null;

    private async Task WakeOrPollAsync(Task ended, CancellationToken ct)
    {
        try
        {
            await ended.WaitAsync(options.WaiterPoll, ct);
        }
        catch (TimeoutException)
        {
            // The safety net: re-read the job.
        }
    }
}
