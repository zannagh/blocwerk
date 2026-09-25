// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Who may train what, in one place: checked when a runner authenticates, when it claims, and again on every
/// job-scoped call (bundle, progress, result), so a revoke, a lost admin right, a withdrawn approval or a banned owner
/// takes effect at the runner's next call, not only at its next claim.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>
    /// Runners that may act at all: not revoked, and the owner is a live account (not deleted/anonymised, not the
    /// Ghost, not locked out).
    /// </summary>
    internal IQueryable<GpuRunner> ActiveRunners(BlocwerkDbContext db)
    {
        var now = Now;
        return db.GpuRunners.Where(r => r.RevokedAt == null && r.OwnerUserId != GhostUser.Id
            && db.Users.IgnoreQueryFilters().Any(u => u.Id == r.OwnerUserId && u.DeletedAt == null
                                                       && (u.LockoutUntil == null || u.LockoutUntil <= now)));
    }

    /// <summary>
    /// The runner-to-wall assignments that count: the runner is active and its owner still administers the wall
    /// (owner or admin member). A runner kept on a wall its owner lost admin rights to never sees that wall's photos.
    /// </summary>
    internal IQueryable<GpuRunnerWall> Assignments(BlocwerkDbContext db)
    {
        var active = ActiveRunners(db);
        return db.GpuRunnerWalls.Where(rw => active.Any(r => r.Id == rw.RunnerId)
            && (db.Walls.IgnoreQueryFilters().Any(w => w.Id == rw.WallId && w.OwnerId == rw.Runner.OwnerUserId)
                || db.WallMembers.Any(m => m.WallId == rw.WallId && m.UserId == rw.Runner.OwnerUserId && m.Role == WallRole.Admin)));
    }

    /// <summary>
    /// The approvals that count: the runner is active and (still) shared by a site admin, and the wall admin who
    /// approved it still administers the wall.
    /// </summary>
    internal IQueryable<GpuRunnerApproval> Approvals(BlocwerkDbContext db)
    {
        var active = ActiveRunners(db);
        return db.GpuRunnerApprovals.Where(a => a.Runner.SharedWithOtherWalls && active.Any(r => r.Id == a.RunnerId)
            && (db.Walls.IgnoreQueryFilters().Any(w => w.Id == a.WallId && w.OwnerId == a.ApprovedByUserId)
                || db.WallMembers.Any(m => m.WallId == a.WallId && m.UserId == a.ApprovedByUserId && m.Role == WallRole.Admin)));
    }

    /// <summary>Whether the runner may (still) train a job of this wall: as the wall's own runner, or shared and approved.</summary>
    internal async Task<bool> MayServeAsync(BlocwerkDbContext db, Guid runnerId, Guid wallId, CancellationToken ct) =>
        await Assignments(db).AnyAsync(rw => rw.RunnerId == runnerId && rw.WallId == wallId, ct)
        || await Approvals(db).AnyAsync(a => a.RunnerId == runnerId && a.WallId == wallId, ct);

    /// <summary>
    /// Re-checks a held job's eligibility; when it is lost, the claim goes back to the queue (free: the runner did
    /// nothing wrong) and the runner is told the job is gone.
    /// </summary>
    private async Task<bool> StillEligibleAsync(BlocwerkDbContext db, GpuRunner runner, GpuJob job, CancellationToken ct)
    {
        if (await MayServeAsync(db, runner.Id, job.WallId, ct))
        {
            return true;
        }

        logger.LogWarning(
            "Runner {RunnerId} ({Name}) may no longer train wall {WallId}; GPU job {JobId} goes back to the queue",
            runner.Id, runner.Name, job.WallId, job.Id);
        await ReleaseAsync(db, job, ReleaseKind.Free, "the 3D runner may no longer train this wall", ct);
        return false;
    }
}
