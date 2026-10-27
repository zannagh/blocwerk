// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Hold detection jobs (<see cref="GpuJobKind.DetectHolds"/>): the detection half of the hold proposal search, run on a
/// 3D runner instead of inside the app. They share the queue, lease, claim token, retry budgets and uploads of the other
/// kinds; only runners that advertise <c>detect-holds</c> are offered one. Nothing in the capture pipeline waits on them:
/// the search that queued the job polls it (<see cref="RunnerHoldDetection"/>) and detects in the app itself when no
/// runner takes it in time, so a job ending here only ends (it never marks the capture without a photo-real view).
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>A hold detection job by id, or null.</summary>
    public async Task<GpuJob?> HoldsJobAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.Kind == GpuJobKind.DetectHolds, ct);
    }

    /// <summary>
    /// The capture's hold detection job for <paramref name="modelId"/> that is still in play (waiting, on a runner, or
    /// delivered and not taken yet), or null: a search resumed after a restart picks it up instead of queueing another.
    /// </summary>
    public async Task<GpuJob?> OpenHoldsJobAsync(Guid captureId, Guid modelId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuJobs.AsNoTracking()
            .Where(j => j.CaptureId == captureId && j.GeometryModelId == modelId && j.Kind == GpuJobKind.DetectHolds
                        && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                            || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null)))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Whether a runner that may serve the wall, finds holds and takes work (online, not paused) is there right now.</summary>
    public Task<bool> HoldsRunnerReadyAsync(Guid wallId, CancellationToken ct) => RunnerReadyAsync(wallId, GpuJobKind.DetectHolds, ct);

    /// <summary>Whether a runner that may serve the wall, takes jobs of <paramref name="kind"/> and takes work (online, not paused) is there now.</summary>
    public async Task<bool> RunnerReadyAsync(Guid wallId, GpuJobKind kind, CancellationToken ct)
    {
        if (options.Mode == GpuRunnerMode.Off)
        {
            return false;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var online = Now - options.OnlineWindow;
        var own = await Assignments(db).Where(rw => rw.WallId == wallId && rw.Runner.LastSeenAt >= online && rw.Runner.Paused != true)
            .Select(rw => rw.Runner.Capabilities).ToListAsync(ct);
        var shared = await Approvals(db).Where(a => a.WallId == wallId && a.Runner.LastSeenAt >= online && a.Runner.Paused != true)
            .Select(a => a.Runner.Capabilities).ToListAsync(ct);
        return own.Concat(shared).Any(c => RunnerCapabilities.Allows(c, kind));
    }

    /// <summary>
    /// Cancels the hold detection job when it is still in <paramref name="states"/> (conditional: a runner may claim or
    /// deliver it meanwhile); its files go. Returns whether it was cancelled.
    /// </summary>
    public async Task<bool> CancelHoldsAsync(Guid jobId, string reason, IReadOnlyCollection<GpuJobStatus> states, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var cancelled = await db.GpuJobs
            .Where(j => j.Id == jobId && j.Kind == GpuJobKind.DetectHolds && states.Contains(j.Status))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Cancelled)
                    .SetProperty(j => j.Error, Clip(reason, 2048))
                    .SetProperty(j => j.CompletedAt, Now)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null),
                ct);
        if (cancelled == 0)
        {
            return false;
        }

        await CloseHoldsAsync(jobId, reason, ct);
        logger.LogInformation("GPU job {JobId} (hold detection) cancelled: {Reason}", jobId, reason);
        return true;
    }

    /// <summary>
    /// A hold detection job is over once the search has read its result (<paramref name="error"/> null) or gave up on it:
    /// its files (bundle, result) go, nothing is kept as a leftover.
    /// </summary>
    public async Task CloseHoldsAsync(Guid jobId, string? error, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.Kind == GpuJobKind.DetectHolds, ct);
        if (job is null)
        {
            return;
        }

        var spent = FilesOf(job).ToList();
        job.ResultPath = null;
        if (error is null)
        {
            job.InstalledAt ??= Now;
            job.Error = null;
        }
        else
        {
            job.Status = job.Status is GpuJobStatus.Cancelled or GpuJobStatus.Failed ? job.Status : GpuJobStatus.Failed;
            job.Error = Clip(error, 2048);
            job.CompletedAt ??= Now;
        }

        await db.SaveChangesAsync(ct);
        DeleteAll(spent, job.Id);
    }

    /// <summary>The stage text of a job whose runner delivered its result.</summary>
    private static string DeliveredStage(GpuJobKind kind) => kind switch
    {
        GpuJobKind.Textures => "rendered; installing on the server",
        GpuJobKind.DetectHolds => "holds found; matching them on the server",
        GpuJobKind.RegisterPanels => "panel photos registered",
        GpuJobKind.SplatPrepare => "prepared; reading the result on the server",
        GpuJobKind.SplatFinish => "finished; reading the result on the server",
        _ => "trained; finishing on the server",
    };

    /// <summary>
    /// A job that ended for good (failed, cancelled, expired): the capture pipeline hears of it the way its kind needs. A
    /// textures job wakes the re-render, a hold detection job nothing (its search polls), a splat prepare / finish job wakes the capture parked on it, a splat job marks the capture.
    /// </summary>
    private async Task JobEndedAsync(BlocwerkDbContext db, GpuJob job, string reason, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case GpuJobKind.Textures:
                NotifyTextures(job.CaptureId);
                break;
            case GpuJobKind.DetectHolds or GpuJobKind.RegisterPanels:
                break;
            case GpuJobKind.SplatPrepare or GpuJobKind.SplatFinish:
                await HandBackAsync(db, job.CaptureId, job.Kind, ct);
                break;
            default:
                await MarkCaptureWithoutSplatAsync(db, job, reason, ct);
                break;
        }
    }

    /// <summary>A delivered hold detection result nobody read for a day (its search is gone): closed, its files deleted.</summary>
    private async Task ExpireHoldsDeliveryAsync(BlocwerkDbContext db, GpuJob job, DateTimeOffset now, CancellationToken ct)
    {
        if (job.CompletedAt < now - FinishGiveUp)
        {
            logger.LogWarning("GPU job {JobId}: the holds a runner found were not used for a day; closing it", job.Id);
            await CloseHoldsAsync(job.Id, "the holds the runner found were not used", ct);
            return;
        }

        await db.GpuJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, now + FinishRetryInterval), ct);
    }

    private string? ValidateStoredHolds(string stored, GpuRunner runner, Guid jobId)
    {
        try
        {
            var path = files.ResolvePhysicalPath(stored) ?? throw new InvalidDataException("The upload was not stored.");
            RunnerHoldsResult.Validate(path);
            return RunnerHoldsResult.Format;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            logger.LogWarning("Runner {RunnerId} sent an invalid hold detection result for GPU job {JobId}: {Reason}", runner.Id, jobId, ex.Message);
            return null;
        }
    }
}
