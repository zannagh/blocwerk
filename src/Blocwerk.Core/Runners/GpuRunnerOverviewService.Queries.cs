// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>The overview's reads: the runners in scope with their walls, the jobs they hold, the recent jobs they failed.</summary>
public sealed partial class GpuRunnerOverviewService
{
    /// <summary>
    /// A site admin: every runner, revoked ones included. Anyone else: the live runners they own or that serve one of their
    /// walls (assigned to it, or shared and approved for it); none without walls or runners.
    /// </summary>
    private static async Task<List<GpuRunnerOverviewInput>> RunnersAsync(BlocwerkDbContext db, GpuRunnerViewer viewer, CancellationToken ct)
    {
        var walls = viewer.Walls.ToList();
        var userId = viewer.UserId;
        var runners = viewer.IsAppAdmin
            ? db.GpuRunners
            : db.GpuRunners.Where(r => r.RevokedAt == null
                && (r.OwnerUserId == userId
                    || db.GpuRunnerWalls.Any(rw => rw.RunnerId == r.Id && walls.Contains(rw.WallId))
                    || (r.SharedWithOtherWalls && db.GpuRunnerApprovals.Any(a => a.RunnerId == r.Id && walls.Contains(a.WallId)))));
        var rows = await runners.AsNoTracking()
            .Select(r => new { Runner = r, Owner = r.Owner.CustomDisplayName ?? r.Owner.DisplayName })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Runner.Id).ToList();
        var own = await db.GpuRunnerWalls.AsNoTracking().Where(rw => ids.Contains(rw.RunnerId))
            .Select(rw => new { rw.RunnerId, rw.WallId, rw.Wall.Name, Approved = false })
            .ToListAsync(ct);
        var approved = await db.GpuRunnerApprovals.AsNoTracking().Where(a => ids.Contains(a.RunnerId) && a.Runner.SharedWithOtherWalls)
            .Select(a => new { a.RunnerId, a.WallId, a.Wall.Name, Approved = true })
            .ToListAsync(ct);
        var served = own.Concat(approved).ToLookup(w => w.RunnerId);
        return rows.Select(r => new GpuRunnerOverviewInput(
                r.Runner,
                string.IsNullOrWhiteSpace(r.Owner) ? "Unknown" : r.Owner,
                served[r.Runner.Id].DistinctBy(w => w.WallId).Select(w => new GpuRunnerWallRef(w.WallId, w.Name, w.Approved)).ToList()))
            .ToList();
    }

    private static Task<List<GpuRunnerHeldJob>> HeldJobsAsync(BlocwerkDbContext db, List<Guid> runnerIds, CancellationToken ct) =>
        db.GpuJobs.AsNoTracking()
            .Where(j => j.ClaimedByRunnerId != null && runnerIds.Contains(j.ClaimedByRunnerId.Value)
                        && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .Select(j => new GpuRunnerHeldJob(j.Id, j.ClaimedByRunnerId!.Value, j.WallId, j.LeaseExpiresAt, j.HeartbeatAt))
            .ToListAsync(ct);

    /// <summary>The newest <see cref="FailureSamples"/> jobs some runner failed, one entry per runner in scope that failed it.</summary>
    private static async Task<List<GpuRunnerFailedJob>> FailedJobsAsync(BlocwerkDbContext db, List<Guid> runnerIds, CancellationToken ct)
    {
        var jobs = await db.GpuJobs.AsNoTracking()
            .Where(j => j.FailedRunnerIdsJson != null)
            .OrderByDescending(j => j.CreatedAt)
            .Take(FailureSamples)
            .Select(j => new { j.WallId, j.FailedRunnerIdsJson, j.CompletedAt, j.ClaimedAt, j.CreatedAt, j.Error })
            .ToListAsync(ct);
        var ids = runnerIds.ToHashSet();
        return jobs
            .SelectMany(j => GpuJobFailedRunners.Parse(j.FailedRunnerIdsJson)
                .Where(ids.Contains)
                .Select(runner => new GpuRunnerFailedJob(runner, j.WallId, j.CompletedAt ?? j.ClaimedAt ?? j.CreatedAt, j.Error)))
            .ToList();
    }
}
