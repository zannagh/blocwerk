// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>Lease expiry, cancellation, and the "waiting for a 3D runner" text of waiting captures.</summary>
public sealed partial class GpuJobQueue
{
    /// <summary>Requeues (or fails) every job whose lease ran out; refreshes the waiting captures' stage. Returns how many expired.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var now = Now;
        var expired = await db.GpuJobs
            .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            logger.LogWarning(
                "GPU job {JobId}: the lease of runner {RunnerId} expired (attempt {Attempt} of {Max})",
                job.Id, job.ClaimedByRunnerId, job.Attempts, options.MaxAttempts);
            await ReleaseAsync(db, job, retry: true, "the 3D runner stopped responding (its lease expired)", ct);
        }

        await RefreshWaitingAsync(db, ct);
        return expired.Count;
    }

    /// <summary>Cancels the capture's waiting or running GPU job; the capture ends without a new splat.</summary>
    public async Task<bool> CancelForCaptureAsync(Guid captureId, string reason, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var jobs = await db.GpuJobs
            .Where(j => j.CaptureId == captureId
                        && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Status = GpuJobStatus.Cancelled;
            job.Error = Clip(reason, 2048);
            job.CompletedAt = Now;
            job.LeaseExpiresAt = null;
            logger.LogInformation("GPU job {JobId} of capture {CaptureId} cancelled: {Reason}", job.Id, captureId, reason);
        }

        await db.SaveChangesAsync(ct);
        await EndCaptureAsync(db, captureId, reason, ct);
        foreach (var job in jobs)
        {
            DeleteFiles(job);
        }

        return jobs.Count > 0;
    }

    /// <summary>Requeues whatever a revoked runner held, right away (its key already stopped working).</summary>
    public async Task ReleaseRunnerAsync(Guid runnerId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var held = await db.GpuJobs
            .Where(j => j.ClaimedByRunnerId == runnerId && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .ToListAsync(ct);
        foreach (var job in held)
        {
            job.Attempts = Math.Max(0, job.Attempts - 1);
            await ReleaseAsync(db, job, retry: true, "the 3D runner was revoked", ct);
        }
    }

    private static async Task EndCaptureAsync(BlocwerkDbContext db, Guid captureId, string reason, CancellationToken ct)
    {
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == captureId, ct);
        if (capture is { Status: WallCaptureStatus.AwaitingRunner })
        {
            WallCaptureProcessor.EndWithoutSplat(capture, reason);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>"Waiting for a 3D runner (none online)" / "(1 online, busy)" on every capture whose job is still queued.</summary>
    private async Task RefreshWaitingAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var waiting = await db.GpuJobs.Where(j => j.Status == GpuJobStatus.Queued)
            .Select(j => new { j.CaptureId, j.WallId }).ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return;
        }

        var online = Now - options.OnlineWindow;
        var live = await db.GpuRunners.Where(r => r.RevokedAt == null && r.LastSeenAt >= online)
            .Select(r => new { r.Id, r.SharedWithOtherWalls }).ToListAsync(ct);
        var liveIds = live.Select(r => r.Id).ToList();
        var own = await db.GpuRunnerWalls.Where(rw => liveIds.Contains(rw.RunnerId)).Select(rw => new { rw.WallId, rw.RunnerId }).ToListAsync(ct);
        foreach (var w in waiting)
        {
            var ownOnline = own.Count(o => o.WallId == w.WallId);
            var count = ownOnline > 0 ? ownOnline : live.Count(r => r.SharedWithOtherWalls);
            var text = count == 0
                ? "Photo-real view: waiting for a 3D runner (none online)"
                : $"Photo-real view: waiting for a 3D runner ({count} online, busy)";
            await SetCaptureStageAsync(db, w.CaptureId, 0, text, ct);
        }
    }
}
