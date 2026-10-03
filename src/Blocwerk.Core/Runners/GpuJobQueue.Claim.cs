// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Claiming: which queued job a runner gets. A runner may take a job up to its quality (draft &lt; high &lt; max &lt;
/// ultra) when it serves the job's wall as the wall's OWN runner (see <see cref="GpuJobQueue.Assignments"/>), or when
/// a site admin shared it, the wall's admin approved THIS runner (<see cref="GpuJobQueue.Approvals"/>) and no own
/// runner of the wall that could train it is online. Own jobs come first, then shared ones; oldest first within each
/// (<see cref="GpuJobQueue.PickAsync"/>). A runner holds at most one claim at a time; after a restart it gets that one
/// back (<see cref="GpuJobQueue.ReattachAsync"/>).
/// </summary>
public sealed partial class GpuJobQueue
{
    private const int CandidateWindow = 200;
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim claimLock = new(1, 1);

    /// <summary>
    /// Eligibility rank of a job for a runner: 0 = the runner is the wall's own, 1 = shared help, null = not allowed.
    /// </summary>
    public static int? Rank(bool servesWall, bool runnerShared, bool wallApprovedRunner, bool wallHasOwnRunnerOnline) =>
        servesWall ? 0 : runnerShared && wallApprovedRunner && !wallHasOwnRunnerOnline ? 1 : null;

    /// <summary>The highest quality a runner may claim: its hello's (unknown = high), capped by what the claim asks.</summary>
    public static SplatQuality QualityCap(string? reported, string? requested)
    {
        var cap = CaptureSplatDocuments.ParseQuality(reported) ?? SplatQuality.High;
        return CaptureSplatDocuments.ParseQuality(requested) is { } asked && asked < cap ? asked : cap;
    }

    /// <summary>Long-poll: a claimed (or re-attached) job, or null after <paramref name="wait"/>.</summary>
    public async Task<RunnerClaim?> ClaimAsync(GpuRunner runner, TimeSpan wait, RunnerClaimRequest? request, CancellationToken ct)
    {
        var deadline = Now + wait;
        if (runner.Paused is true)
        {
            await ClearPausedAsync(runner.Id, ct);
        }

        while (true)
        {
            var (job, reattached) = await TryClaimOrReattachAsync(runner, request, ct);
            if (job is not null)
            {
                return new RunnerClaim(
                    job.Id, CaptureSplatDocuments.QualityName(job.Quality),
                    (int)options.Lease.TotalSeconds, job.BundleBytes, job.BundleSha256, options.Previews, reattached,
                    job.WallId, job.CaptureId);
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
    public Task<GpuJob?> TryClaimAsync(GpuRunner runner, string? maxQuality, CancellationToken ct) =>
        TryClaimRequestAsync(runner, new RunnerClaimRequest(maxQuality), ct);

    /// <summary>
    /// One claim attempt: a queued job, or the job this runner already holds when it has been silent long enough to have
    /// restarted (<see cref="ReattachAsync"/>). Public for tests.
    /// </summary>
    public async Task<GpuJob?> TryClaimRequestAsync(GpuRunner runner, RunnerClaimRequest? request, CancellationToken ct) =>
        (await TryClaimOrReattachAsync(runner, request, ct)).Job;

    /// <summary>
    /// Whether the <see cref="GpuRunnerMode.Auto"/> mode sends this wall's photo-real view to a runner: one that may
    /// train it at this quality is online now (the wall's own, or a shared one the wall approved).
    /// </summary>
    public async Task<bool> HasEligibleRunnerOnlineAsync(Guid wallId, SplatQuality quality, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var online = Now - options.OnlineWindow;
        var own = await Assignments(db).Where(rw => rw.WallId == wallId && rw.Runner.LastSeenAt >= online && rw.Runner.Paused != true)
            .Select(rw => rw.Runner.MaxQuality).ToListAsync(ct);
        var shared = await Approvals(db).Where(a => a.WallId == wallId && a.Runner.LastSeenAt >= online && a.Runner.Paused != true)
            .Select(a => a.Runner.MaxQuality).ToListAsync(ct);
        return own.Concat(shared).Any(q => QualityCap(q, null) >= quality);
    }

    /// <summary>Whether some runner that may serve this wall reports <see cref="SplatQuality.Ultra"/> (online or not).</summary>
    public async Task<bool> UltraAvailableForWallAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var ultra = CaptureSplatDocuments.QualityName(SplatQuality.Ultra);
        return await Assignments(db).AnyAsync(rw => rw.WallId == wallId && rw.Runner.MaxQuality == ultra, ct)
               || await Approvals(db).AnyAsync(a => a.WallId == wallId && a.Runner.MaxQuality == ultra, ct);
    }

    /// <summary>A runner that claims is not paused (any more), whatever its last hello said.</summary>
    private async Task ClearPausedAsync(Guid runnerId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        await db.GpuRunners.Where(r => r.Id == runnerId && r.Paused == true)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Paused, false), ct);
    }

    private static Task<bool> HoldsClaimAsync(BlocwerkDbContext db, Guid runnerId, CancellationToken ct) =>
        db.GpuJobs.AnyAsync(
            j => j.ClaimedByRunnerId == runnerId && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running), ct);

    private async Task<GpuJob?> ClaimPickAsync(BlocwerkDbContext db, GpuRunner runner, GpuJob pick, CancellationToken ct)
    {
        var now = Now;
        var lease = now + options.Lease;
        var claimed = await db.GpuJobs
            .Where(j => j.Id == pick.Id && j.Status == GpuJobStatus.Queued)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Claimed)
                    .SetProperty(j => j.ClaimedByRunnerId, runner.Id)
                    .SetProperty(j => j.ClaimedAt, now)
                    .SetProperty(j => j.HeartbeatAt, now)
                    .SetProperty(j => j.ClaimToken, Clip(runner.ClaimToken, 64))
                    .SetProperty(j => j.LeaseExpiresAt, lease)
                    .SetProperty(j => j.Progress, 0)
                    .SetProperty(j => j.Stage, "is downloading the photos")
                    .SetProperty(j => j.Error, (string?)null)
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1),
                ct);
        if (claimed == 0)
        {
            return null;
        }

        await db.GpuRunners.Where(r => r.Id == runner.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastJobAt, now), ct);
        logger.LogInformation(
            "Runner {RunnerId} ({Name}) claimed GPU job {JobId} of wall {WallId} ({Quality}, claim {Claim})",
            runner.Id, runner.Name, pick.Id, pick.WallId, pick.Quality, pick.Attempts + 1);
        return await db.GpuJobs.AsNoTracking().FirstAsync(j => j.Id == pick.Id, ct);
    }

    private async Task<(GpuJob? Job, bool Reattached)> TryClaimOrReattachAsync(GpuRunner runner, RunnerClaimRequest? request, CancellationToken ct)
    {
        await claimLock.WaitAsync(ct);
        try
        {
            await using var db = dbContextFactory.CreateDbContext();

            // Re-read: a long-poll outlives a revoke, a new hello, a change of the shared flag or a banned owner.
            var current = await ActiveRunners(db).AsNoTracking().FirstOrDefaultAsync(r => r.Id == runner.Id, ct);
            if (current is null)
            {
                return (null, false);
            }

            current.ClaimToken = runner.ClaimToken;
            if (await HoldsClaimAsync(db, runner.Id, ct))
            {
                var held = await ReattachAsync(db, current, ct);
                return (held, held is not null);
            }

            var cap = QualityCap(current.MaxQuality, request?.MaxQuality);
            var pick = await PickAsync(db, current, cap, request?.MaxBundleBytes is > 0 ? request.MaxBundleBytes : null, ct);
            return (pick is null ? null : await ClaimPickAsync(db, runner, pick, ct), false);
        }
        finally
        {
            claimLock.Release();
        }
    }
}
