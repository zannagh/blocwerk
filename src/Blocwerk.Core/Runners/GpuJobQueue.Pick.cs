// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Which queued job a claiming runner gets. Besides quality and eligibility (<see cref="Rank"/>): a job whose bundle is
/// larger than the runner downloads is not offered to it, and a job this runner already failed
/// (<see cref="GpuJobFailedRunners"/>) is left to another runner that may train it and is online and idle, so a retry
/// does not land on the very card that just ran out of memory. The job never waits on that for long: with no such
/// runner, or once <see cref="LeaveToOthersFor"/> has passed since the failed claim without one taking it (it may refuse
/// it for reasons the server does not know, such as the bundle size), the runner that failed it may try again.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>How long a job a runner failed is left to other runners before that runner may take it again anyway.</summary>
    internal static readonly TimeSpan LeaveToOthersFor = TimeSpan.FromMinutes(15);

    private async Task<GpuJob?> PickAsync(
        BlocwerkDbContext db, GpuRunner runner, SplatQuality cap, long? maxBundleBytes, CancellationToken ct)
    {
        var served = await Assignments(db).Where(rw => rw.RunnerId == runner.Id).Select(rw => rw.WallId).ToListAsync(ct);
        var queued = db.GpuJobs.AsNoTracking().Where(j => j.Status == GpuJobStatus.Queued && j.Quality <= cap);
        if (maxBundleBytes is { } max)
        {
            queued = queued.Where(j => j.BundleBytes <= max);
        }

        var own = await queued.Where(j => served.Contains(j.WallId)).OrderBy(j => j.CreatedAt).Take(CandidateWindow).ToListAsync(ct);
        var ownPick = await FirstNotLeftToOthersAsync(db, runner.Id, own, ct);
        if (ownPick is not null || !runner.SharedWithOtherWalls)
        {
            return ownPick;
        }

        // Only walls whose admin approved THIS runner are candidates at all.
        var approved = await Approvals(db).Where(a => a.RunnerId == runner.Id).Select(a => a.WallId).ToListAsync(ct);
        var candidates = await queued.Where(j => !served.Contains(j.WallId) && approved.Contains(j.WallId))
            .OrderBy(j => j.CreatedAt).Take(CandidateWindow).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return null;
        }

        var walls = candidates.Select(j => j.WallId).Distinct().ToList();
        var online = Now - options.OnlineWindow;
        var ownOnline = await Assignments(db)
            .Where(rw => walls.Contains(rw.WallId) && rw.RunnerId != runner.Id && rw.Runner.LastSeenAt >= online)
            .Select(rw => new { rw.WallId, rw.RunnerId, rw.Runner.MaxQuality }).ToListAsync(ct);

        // An own runner that already failed the job does not keep a shared one from helping.
        var eligible = candidates.Where(j => Rank(
            false, true, true,
            ownOnline.Any(o => o.WallId == j.WallId && QualityCap(o.MaxQuality, null) >= j.Quality
                               && !GpuJobFailedRunners.Contains(j, o.RunnerId))) is not null).ToList();
        return await FirstNotLeftToOthersAsync(db, runner.Id, eligible, ct);
    }

    /// <summary>
    /// The first of <paramref name="jobs"/> (oldest first) for <paramref name="runnerId"/>: one it did not fail, or one it
    /// failed that no other runner online may train (then it tries again; the attempt budget still ends it).
    /// </summary>
    private async Task<GpuJob?> FirstNotLeftToOthersAsync(BlocwerkDbContext db, Guid runnerId, List<GpuJob> jobs, CancellationToken ct)
    {
        foreach (var job in jobs)
        {
            var failed = GpuJobFailedRunners.Parse(job.FailedRunnerIdsJson);
            var waitedLongEnough = Now - (job.ClaimedAt ?? job.CreatedAt) >= LeaveToOthersFor;
            if (!failed.Contains(runnerId) || waitedLongEnough || !await AnotherRunnerIdleAsync(db, job, failed, ct))
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a runner that did not fail <paramref name="job"/>, may train it (its wall's own or an approved one) at its
    /// quality, is online and holds no claim right now.
    /// </summary>
    private async Task<bool> AnotherRunnerIdleAsync(BlocwerkDbContext db, GpuJob job, IReadOnlyList<Guid> failed, CancellationToken ct)
    {
        var online = Now - options.OnlineWindow;
        var busy = db.GpuJobs.Where(j => j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running);
        var own = await Assignments(db)
            .Where(rw => rw.WallId == job.WallId && rw.Runner.LastSeenAt >= online && !failed.Contains(rw.RunnerId)
                         && !busy.Any(j => j.ClaimedByRunnerId == rw.RunnerId))
            .Select(rw => rw.Runner.MaxQuality).ToListAsync(ct);
        var shared = await Approvals(db)
            .Where(a => a.WallId == job.WallId && a.Runner.LastSeenAt >= online && !failed.Contains(a.RunnerId)
                        && !busy.Any(j => j.ClaimedByRunnerId == a.RunnerId))
            .Select(a => a.Runner.MaxQuality).ToListAsync(ct);
        return own.Concat(shared).Any(q => QualityCap(q, null) >= job.Quality);
    }
}
