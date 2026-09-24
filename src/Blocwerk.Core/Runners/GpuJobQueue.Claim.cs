// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Claiming: which queued job a runner gets. A runner may take a job when it serves the job's wall
/// (the wall's OWN runner), or when it is shared and the wall has no own runner online. Own jobs
/// come first, then shared ones; oldest first within each.
/// </summary>
public sealed partial class GpuJobQueue
{
    private const int CandidateWindow = 200;
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim claimLock = new(1, 1);

    /// <summary>
    /// Eligibility rank of a job for a runner: 0 = the runner is the wall's own, 1 = shared help,
    /// null = not allowed.
    /// </summary>
    public static int? Rank(bool servesWall, bool runnerShared, bool wallHasOwnRunnerOnline) =>
        servesWall ? 0 : runnerShared && !wallHasOwnRunnerOnline ? 1 : null;

    /// <summary>Long-poll: a claimed job, or null after <see cref="GpuRunnerOptions.ClaimWait"/>.</summary>
    public async Task<RunnerClaim?> ClaimAsync(GpuRunner runner, TimeSpan wait, CancellationToken ct)
    {
        var deadline = Now + wait;
        while (true)
        {
            var job = await TryClaimAsync(runner, ct);
            if (job is not null)
            {
                return new RunnerClaim(
                    job.Id, CaptureSplatDocuments.QualityName(job.Quality).ToLowerInvariant(),
                    (int)options.Lease.TotalSeconds, job.BundleBytes, job.BundleSha256);
            }

            var left = deadline - Now;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            await signal.WaitAsync(left < RecheckInterval ? left : RecheckInterval, ct);
        }
    }

    /// <summary>One claim attempt. Public for tests.</summary>
    public async Task<GpuJob?> TryClaimAsync(GpuRunner runner, CancellationToken ct)
    {
        await claimLock.WaitAsync(ct);
        try
        {
            await using var db = dbContextFactory.CreateDbContext();

            // Re-read: a long-poll outlives a revoke or a change of the shared flag.
            var current = await db.GpuRunners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runner.Id && r.RevokedAt == null, ct);
            var pick = current is null ? null : await PickAsync(db, current, ct);
            if (pick is null)
            {
                return null;
            }

            var now = Now;
            var lease = now + options.Lease;
            var claimed = await db.GpuJobs
                .Where(j => j.Id == pick.Id && j.Status == GpuJobStatus.Queued)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, GpuJobStatus.Claimed)
                        .SetProperty(j => j.ClaimedByRunnerId, runner.Id)
                        .SetProperty(j => j.ClaimedAt, now)
                        .SetProperty(j => j.LeaseExpiresAt, lease)
                        .SetProperty(j => j.Progress, 0)
                        .SetProperty(j => j.Stage, "claimed")
                        .SetProperty(j => j.Attempts, j => j.Attempts + 1),
                    ct);
            if (claimed == 0)
            {
                return null;
            }

            await db.GpuRunners.Where(r => r.Id == runner.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastJobAt, now), ct);
            await SetCaptureStageAsync(db, pick.CaptureId, 0, $"Photo-real view: 3D runner “{runner.Name}” is downloading the photos", ct);
            logger.LogInformation(
                "Runner {RunnerId} ({Name}) claimed GPU job {JobId} of wall {WallId} (attempt {Attempt})",
                runner.Id, runner.Name, pick.Id, pick.WallId, pick.Attempts + 1);
            return await db.GpuJobs.AsNoTracking().FirstAsync(j => j.Id == pick.Id, ct);
        }
        finally
        {
            claimLock.Release();
        }
    }

    private async Task<GpuJob?> PickAsync(BlocwerkDbContext db, GpuRunner runner, CancellationToken ct)
    {
        var served = await db.GpuRunnerWalls.Where(rw => rw.RunnerId == runner.Id).Select(rw => rw.WallId).ToListAsync(ct);
        var queued = db.GpuJobs.AsNoTracking().Where(j => j.Status == GpuJobStatus.Queued);
        var own = await queued.Where(j => served.Contains(j.WallId)).OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (own is not null || !runner.SharedWithOtherWalls)
        {
            return own;
        }

        var candidates = await queued.OrderBy(j => j.CreatedAt).Take(CandidateWindow).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return null;
        }

        var walls = candidates.Select(j => j.WallId).Distinct().ToList();
        var online = Now - options.OnlineWindow;
        var covered = await db.GpuRunnerWalls
            .Where(rw => walls.Contains(rw.WallId) && rw.RunnerId != runner.Id
                         && rw.Runner.RevokedAt == null && rw.Runner.LastSeenAt >= online)
            .Select(rw => rw.WallId).Distinct().ToListAsync(ct);
        return candidates.FirstOrDefault(j => Rank(false, true, covered.Contains(j.WallId)) is not null);
    }

    private static async Task SetCaptureStageAsync(BlocwerkDbContext db, Guid captureId, double progress, string stage, CancellationToken ct)
    {
        var clipped = stage.Length <= 200 ? stage : stage[..200];
        await db.WallCaptures
            .Where(c => c.Id == captureId && c.Status == WallCaptureStatus.AwaitingRunner)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Stage, clipped).SetProperty(c => c.Progress, Math.Clamp(progress, 0, 1)), ct);
    }
}
