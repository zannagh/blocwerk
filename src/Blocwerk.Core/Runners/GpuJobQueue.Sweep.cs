// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>Lease expiry, the queued jobs' lifetime, and the "waiting for a 3D runner" text of queued jobs.</summary>
public sealed partial class GpuJobQueue
{
    /// <summary>
    /// Requeues (or fails) every job whose lease ran out, cancels jobs that waited longer than
    /// <see cref="GpuRunnerOptions.QueuedLifetime"/>, hands pending previews to the preview worker again now and then,
    /// and refreshes the queued jobs' text. Returns how many leases expired.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var now = Now;
        var expired = await db.GpuJobs.AsNoTracking()
            .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            await ExpireLeaseAsync(db, job, now, ct);
        }

        await CancelDisabledComputeJobsAsync(db, now, ct);
        await ExpireQueuedAsync(db, now, ct);
        await RetryUnfinishedAsync(db, now, ct);
        await RetryPendingPreviewsAsync(now, ct);
        await RefreshWaitingAsync(db, ct);
        return expired.Count;
    }

    /// <summary>
    /// The queued job's line for <paramref name="online"/> able runners, <paramref name="busy"/> of them on another job;
    /// with none available but <paramref name="paused"/> able ones online and paused by their owner, "(paused)".
    /// </summary>
    internal static string WaitingStage(
        SplatQuality quality, int online, int busy, int paused = 0, GpuJobKind kind = GpuJobKind.Splat, bool markerless = false) =>
        online == 0 && paused > 0 ? "waiting for a 3D runner (paused)"
        : online == 0 && kind == GpuJobKind.Textures ? "waiting for a 3D runner that can render wall textures (none online)"
        : online == 0 && kind == GpuJobKind.DetectHolds ? "waiting for a 3D runner that can find holds (none online)"
        : online == 0 && kind == GpuJobKind.RegisterPanels ? "waiting for a 3D runner that can register panel photos (none online)"
        : online == 0 && kind == GpuJobKind.SplatPrepare && markerless ? "waiting for a 3D runner to reconstruct the wall (none online)"
        : online == 0 && kind == GpuJobKind.SplatPrepare ? "waiting for a 3D runner to prepare the photos (none online)"
        : online == 0 && kind == GpuJobKind.SplatFinish ? "waiting for a 3D runner to finish it (none online)"
        : online == 0 ? $"waiting for a 3D runner that can train {CaptureSplatDocuments.QualityName(quality)} (none online)"
        : busy >= online ? $"waiting for a 3D runner ({online} online, busy)"
        : busy == 0 ? $"waiting for a 3D runner ({online} online)"
        : $"waiting for a 3D runner ({online} online, {busy} busy)";

    private async Task ExpireLeaseAsync(BlocwerkDbContext db, GpuJob job, DateTimeOffset now, CancellationToken ct)
    {
        var uploading = IsUploadingStage(job.Stage);
        if (job.ClaimedAt is { } claimed && Deadline(claimed, uploading, job.Kind) <= now)
        {
            // Heartbeats alone never keep a claim past the wall-clock cap; that costs a training attempt.
            logger.LogWarning("GPU job {JobId}: runner {RunnerId} held it past the cap: {Reason}", job.Id, job.ClaimedByRunnerId, TooLongReason(job.Kind));
            await ReleaseAsync(db, job, ReleaseKind.Failure, TooLongReason(job.Kind), ct);
            return;
        }

        logger.LogWarning(
            "GPU job {JobId}: the lease of runner {RunnerId} expired (lost {Lost} of {Max})",
            job.Id, job.ClaimedByRunnerId, job.LostLeaseCount + 1, options.MaxLostLeases);
        await ReleaseAsync(db, job, ReleaseKind.LostLease, "the 3D runner stopped responding (its lease expired)", ct);
    }

    /// <summary>
    /// Jobs nobody claimed within <see cref="GpuRunnerOptions.QueuedLifetime"/>: cancelled and their files (the bundle is
    /// a copy of the capture's photos) deleted, so no runner receives them months later.
    /// </summary>
    private async Task ExpireQueuedAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.GpuJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued && j.CreatedAt < now - ShortestQueuedLifetime).ToListAsync(ct);
        var markerless = await MarkerlessCapturesAsync(db, candidates, ct);
        foreach (var job in candidates)
        {
            var lifetime = QueuedLifetimeOf(job, markerless);
            if (job.CreatedAt >= now - lifetime)
            {
                continue;
            }

            var days = lifetime.TotalDays.ToString("0.#", CultureInfo.InvariantCulture);
            var reason = ExpiredReason(job.Kind, markerless.Contains(job.CaptureId), days);
            var cancelled = await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == GpuJobStatus.Queued)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, GpuJobStatus.Cancelled)
                        .SetProperty(j => j.Error, reason)
                        .SetProperty(j => j.CompletedAt, now)
                        .SetProperty(j => j.PreviewPath, (string?)null),
                    ct);
            if (cancelled == 0)
            {
                continue;
            }

            PulseJob(job.WallId, job.Id, null, ended: true);
            logger.LogInformation("GPU job {JobId} of capture {CaptureId} expired: {Reason}", job.Id, job.CaptureId, reason);
            await JobEndedAsync(db, job, reason, ct);

            // An installed preview stays the job's leftover: it may still be the capture's view, to finish again.
            DeleteAll(KeepOnlyInstalledPreview(job), job.Id);
        }
    }

    /// <summary>Why a job nobody claimed for <paramref name="days"/> days was cancelled.</summary>
    private static string ExpiredReason(GpuJobKind kind, bool markerless, string days) => kind switch
    {
        GpuJobKind.Textures => $"no 3D runner rendered the wall textures within {days} days",
        GpuJobKind.DetectHolds => $"no 3D runner looked for holds within {days} days",
        GpuJobKind.RegisterPanels => $"no 3D runner registered the panel photos within {days} days",
        GpuJobKind.SplatPrepare when markerless => $"no 3D runner reconstructed the wall within {days} days",
        GpuJobKind.SplatPrepare => $"no 3D runner prepared the photos within {days} days",
        GpuJobKind.SplatFinish => $"no 3D runner finished the 3D view within {days} days",
        _ => $"no 3D runner took the photo-real view within {days} days",
    };

    /// <summary>
    /// Delivered results the splat worker could not finish yet (it was unreachable): handed back to the capture pipeline
    /// every <see cref="FinishRetryInterval"/>, and given up after <see cref="FinishGiveUp"/>.
    /// </summary>
    private async Task RetryUnfinishedAsync(BlocwerkDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.GpuJobs
            .Where(j => j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in due)
        {
            if (job.Kind == GpuJobKind.Textures)
            {
                await RetryTexturesDeliveryAsync(db, job, now, ct);
                continue;
            }

            if (job.Kind == GpuJobKind.DetectHolds)
            {
                await ExpireHoldsDeliveryAsync(db, job, now, ct);
                continue;
            }

            if (job.Kind is GpuJobKind.SplatPrepare or GpuJobKind.SplatFinish)
            {
                await RetryComputeDeliveryAsync(db, job, now, ct);
                continue;
            }

            if (job.CompletedAt < now - FinishGiveUp)
            {
                // The capture's processor gives up (it tells the capture why, or restores a re-finish's state); only a
                // job without a capture to tell is closed here.
                logger.LogWarning("GPU job {JobId}: the delivered result could not be finished for a day; giving up", job.Id);
                job.LeaseExpiresAt = now + FinishRetryInterval;
                await db.SaveChangesAsync(ct);
                if (await HandBackAsync(db, job.CaptureId, job.Kind, ct))
                {
                    continue;
                }

                var pending = job.PreviewPath;
                job.PreviewPath = null;
                if (GpuJobRefinishState.Parse(job.RefinishStateJson) is { } before)
                {
                    before.RestoreJob(job);
                }
                else
                {
                    job.Status = GpuJobStatus.Failed;
                    job.Error = "the trained view could not be finished on the server (the splat worker was unreachable)";
                }

                await db.SaveChangesAsync(ct);
                DeleteSpent(job, pending);
                continue;
            }

            job.LeaseExpiresAt = now + FinishRetryInterval;
            await db.SaveChangesAsync(ct);
            await HandBackAsync(db, job.CaptureId, job.Kind, ct);
        }
    }

    /// <summary>
    /// A job that failed for good: a finished capture whose view it was to be says so (Model ready, no photo-real view),
    /// exactly like a failed training on the splat worker; with a preview installed it says the preview stays instead. A
    /// capture that moved on (a retrain) is left alone.
    /// </summary>
    private static async Task MarkCaptureWithoutSplatAsync(BlocwerkDbContext db, GpuJob job, string reason, CancellationToken ct)
    {
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == job.CaptureId, ct);
        if (capture is not { Status: WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutTextures })
        {
            return;
        }

        var preview = await db.GpuJobs.AsNoTracking().Where(j => j.Id == job.Id)
            .Select(j => new { j.PreviewInstalledStep, j.TotalSteps }).FirstOrDefaultAsync(ct);
        if (preview?.PreviewInstalledStep is { } step)
        {
            WallCaptureProcessor.KeepPreview(capture, reason, step, preview.TotalSteps);
        }
        else
        {
            WallCaptureProcessor.EndWithoutSplat(capture, reason);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// "waiting for a 3D runner (none online)" / "(1 online, busy)" on every queued job, from the runners' current
    /// heartbeat (a runner that reported its shutdown is offline at once) and whether each one holds a claim right now.
    /// </summary>
    private async Task RefreshWaitingAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var waiting = await db.GpuJobs.AsNoTracking().Where(j => j.Status == GpuJobStatus.Queued).ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return;
        }

        var online = Now - options.OnlineWindow;
        var walls = waiting.Select(j => j.WallId).Distinct().ToList();
        var markerless = await MarkerlessCapturesAsync(db, waiting, ct);

        // Paused runners are online but take no work: they only change "none online" into "paused".
        var own = await Assignments(db).Where(rw => walls.Contains(rw.WallId) && rw.Runner.LastSeenAt >= online)
            .Select(rw => new { rw.WallId, rw.RunnerId, rw.Runner.MaxQuality, rw.Runner.Capabilities, rw.Runner.TexturesMemoryMb, rw.Runner.MemoryBudgetMb, Paused = rw.Runner.Paused == true }).ToListAsync(ct);
        var shared = await Approvals(db).Where(a => walls.Contains(a.WallId) && a.Runner.LastSeenAt >= online)
            .Select(a => new { a.WallId, a.RunnerId, a.Runner.MaxQuality, a.Runner.Capabilities, a.Runner.TexturesMemoryMb, a.Runner.MemoryBudgetMb, Paused = a.Runner.Paused == true }).ToListAsync(ct);
        var busy = (await db.GpuJobs.AsNoTracking()
                .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.ClaimedByRunnerId != null)
                .Select(j => j.ClaimedByRunnerId!.Value).ToListAsync(ct))
            .ToHashSet();
        foreach (var job in waiting)
        {
            var candidates = own.Concat(shared)
                .Where(o => o.WallId == job.WallId && Able(job, o.MaxQuality, o.Capabilities, o.TexturesMemoryMb, o.MemoryBudgetMb)).ToList();
            var able = candidates.Where(o => !o.Paused).Select(o => o.RunnerId).Distinct().ToList();
            var paused = candidates.Where(o => o.Paused).Select(o => o.RunnerId).Distinct().Count();
            var stage = WaitingStage(job.Quality, able.Count, able.Count(busy.Contains), paused, job.Kind, markerless.Contains(job.CaptureId));
            stage = await NoRunnerStageAsync(db, job, stage, able.Count == 0, markerless.Contains(job.CaptureId), ct);

            // Conditional: a runner may have claimed it meanwhile (its stage then says so).
            await SetWaitingStageAsync(db, job, stage, ct);
        }
    }
}
