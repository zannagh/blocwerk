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
/// it is shared, the wall accepted shared runners and no own runner of the wall that could train it is online.
/// Own jobs come first, then shared ones; oldest first within each.
/// </summary>
public sealed partial class GpuJobQueue
{
    private const int CandidateWindow = 200;
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim claimLock = new(1, 1);

    /// <summary>
    /// Eligibility rank of a job for a runner: 0 = the runner is the wall's own, 1 = shared help, null = not allowed.
    /// </summary>
    public static int? Rank(bool servesWall, bool runnerShared, bool wallAcceptsShared, bool wallHasOwnRunnerOnline) =>
        servesWall ? 0 : runnerShared && wallAcceptsShared && !wallHasOwnRunnerOnline ? 1 : null;

    /// <summary>The highest quality a runner may claim: its hello's (unknown = high), capped by what the claim asks.</summary>
    public static SplatQuality QualityCap(string? reported, string? requested)
    {
        var cap = CaptureSplatDocuments.ParseQuality(reported) ?? SplatQuality.High;
        return CaptureSplatDocuments.ParseQuality(requested) is { } asked && asked < cap ? asked : cap;
    }

    /// <summary>Long-poll: a claimed job, or null after <paramref name="wait"/>.</summary>
    public async Task<RunnerClaim?> ClaimAsync(GpuRunner runner, TimeSpan wait, string? maxQuality, CancellationToken ct)
    {
        var deadline = Now + wait;
        while (true)
        {
            var job = await TryClaimAsync(runner, maxQuality, ct);
            if (job is not null)
            {
                return new RunnerClaim(
                    job.Id, CaptureSplatDocuments.QualityName(job.Quality),
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
    public async Task<GpuJob?> TryClaimAsync(GpuRunner runner, string? maxQuality, CancellationToken ct)
    {
        await claimLock.WaitAsync(ct);
        try
        {
            await using var db = dbContextFactory.CreateDbContext();

            // Re-read: a long-poll outlives a revoke, a new hello or a change of the shared flag.
            var current = await db.GpuRunners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runner.Id && r.RevokedAt == null, ct);
            var pick = current is null ? null : await PickAsync(db, current, QualityCap(current.MaxQuality, maxQuality), ct);
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
                        .SetProperty(j => j.Stage, "is downloading the photos")
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
        finally
        {
            claimLock.Release();
        }
    }

    /// <summary>
    /// Whether the <see cref="GpuRunnerMode.Auto"/> mode sends this wall's photo-real view to a runner: one that may
    /// train it at this quality is online now (the wall's own, or a shared one the wall accepted).
    /// </summary>
    public async Task<bool> HasEligibleRunnerOnlineAsync(Guid wallId, SplatQuality quality, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var online = Now - options.OnlineWindow;
        var own = await Assignments(db).Where(rw => rw.WallId == wallId && rw.Runner.LastSeenAt >= online)
            .Select(rw => rw.Runner.MaxQuality).ToListAsync(ct);
        var shared = await AcceptsSharedAsync(db, wallId, ct)
            ? await db.GpuRunners.Where(r => r.RevokedAt == null && r.SharedWithOtherWalls && r.LastSeenAt >= online)
                .Select(r => r.MaxQuality).ToListAsync(ct)
            : [];
        return own.Concat(shared).Any(q => QualityCap(q, null) >= quality);
    }

    /// <summary>Whether some runner that may serve this wall reports <see cref="SplatQuality.Ultra"/> (online or not).</summary>
    public async Task<bool> UltraAvailableForWallAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var ultra = CaptureSplatDocuments.QualityName(SplatQuality.Ultra);
        if (await Assignments(db).AnyAsync(rw => rw.WallId == wallId && rw.Runner.MaxQuality == ultra, ct))
        {
            return true;
        }

        return await AcceptsSharedAsync(db, wallId, ct)
               && await db.GpuRunners.AnyAsync(r => r.RevokedAt == null && r.SharedWithOtherWalls && r.MaxQuality == ultra, ct);
    }

    /// <summary>
    /// The runner-to-wall assignments that count: the runner is not revoked and its owner still administers the wall
    /// (owner or admin member). A runner kept on a wall its owner lost admin rights to never sees that wall's photos.
    /// </summary>
    internal static IQueryable<GpuRunnerWall> Assignments(BlocwerkDbContext db) =>
        db.GpuRunnerWalls.Where(rw => rw.Runner.RevokedAt == null
            && (db.Walls.IgnoreQueryFilters().Any(w => w.Id == rw.WallId && w.OwnerId == rw.Runner.OwnerUserId)
                || db.WallMembers.Any(m => m.WallId == rw.WallId && m.UserId == rw.Runner.OwnerUserId && m.Role == Enums.WallRole.Admin)));

    private async Task<bool> AcceptsSharedAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        !options.SharedNeedsOptIn || await db.GpuRunnerSharedOptIns.AnyAsync(o => o.WallId == wallId, ct);

    private async Task<GpuJob?> PickAsync(BlocwerkDbContext db, GpuRunner runner, SplatQuality cap, CancellationToken ct)
    {
        var served = await Assignments(db).Where(rw => rw.RunnerId == runner.Id).Select(rw => rw.WallId).ToListAsync(ct);
        var queued = db.GpuJobs.AsNoTracking().Where(j => j.Status == GpuJobStatus.Queued && j.Quality <= cap);
        var own = await queued.Where(j => served.Contains(j.WallId)).OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (own is not null || !runner.SharedWithOtherWalls)
        {
            return own;
        }

        var candidates = await queued.Where(j => !served.Contains(j.WallId)).OrderBy(j => j.CreatedAt).Take(CandidateWindow).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return null;
        }

        var walls = candidates.Select(j => j.WallId).Distinct().ToList();
        var accepting = options.SharedNeedsOptIn
            ? await db.GpuRunnerSharedOptIns.Where(o => walls.Contains(o.WallId)).Select(o => o.WallId).ToListAsync(ct)
            : walls;
        var online = Now - options.OnlineWindow;
        var ownOnline = await Assignments(db)
            .Where(rw => walls.Contains(rw.WallId) && rw.RunnerId != runner.Id && rw.Runner.LastSeenAt >= online)
            .Select(rw => new { rw.WallId, rw.Runner.MaxQuality }).ToListAsync(ct);
        return candidates.FirstOrDefault(j => Rank(
            false, true, accepting.Contains(j.WallId),
            ownOnline.Any(o => o.WallId == j.WallId && QualityCap(o.MaxQuality, null) >= j.Quality)) is not null);
    }
}
