// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>The read side: the wall panel's list, the wall's jobs, the site-admin list.</summary>
public sealed partial class GpuRunnerService
{
    public async Task<IReadOnlyList<GpuRunnerInfo>> ListForWallAsync(Guid wallId)
    {
        var (db, userId) = await OpenForWallAdminAsync(wallId);
        await using (db)
        {
            var runners = db.GpuRunners.Where(r => r.RevokedAt == null
                && (r.SharedWithOtherWalls || r.OwnerUserId == userId || db.GpuRunnerWalls.Any(rw => rw.RunnerId == r.Id && rw.WallId == wallId)));
            return await ProjectAsync(db, runners, userId, wallId);
        }
    }

    public async Task<IReadOnlyList<GpuJobInfo>> ListJobsForWallAsync(Guid wallId)
    {
        var (db, _) = await OpenForWallAdminAsync(wallId);
        await using (db)
        {
            var jobs = await db.GpuJobs.AsNoTracking()
                .Where(j => j.WallId == wallId
                            && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                                || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null)))
                .OrderBy(j => j.CreatedAt)
                .Select(j => new { Job = j, Runner = j.ClaimedByRunner == null ? null : j.ClaimedByRunner.Name })
                .ToListAsync();
            return jobs.Select(x => new GpuJobInfo(
                x.Job.Id, x.Job.CaptureId, CaptureSplatDocuments.QualityName(x.Job.Quality), x.Job.Status.ToString(),
                x.Job.Progress, x.Job.Stage, x.Job.Attempts, x.Job.CreatedAt, x.Runner)).ToList();
        }
    }

    public async Task<IReadOnlyList<GpuRunnerInfo>> ListAllAsync()
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
        await AppAdminGuard.EnsureAppAdminAsync(db, user.Id, CancellationToken.None);
        return await ProjectAsync(db, db.GpuRunners, user.Id, null);
    }

    private async Task<List<GpuRunnerInfo>> ProjectAsync(BlocwerkDbContext db, IQueryable<GpuRunner> runners, Guid userId, Guid? wallId)
    {
        // Authorized already; the wall names of a runner's current job must not vanish behind the membership filter.
        db.CurrentUserId = Guid.Empty;
        var rows = await runners.AsNoTracking()
            .Select(r => new
            {
                Runner = r,
                Owner = r.Owner.CustomDisplayName ?? r.Owner.DisplayName,
                Walls = db.GpuRunnerWalls.Where(rw => rw.RunnerId == r.Id).Select(rw => rw.WallId).ToList(),
                Approved = wallId != null && db.GpuRunnerApprovals.Any(a => a.RunnerId == r.Id && a.WallId == wallId),
            })
            .ToListAsync();
        var ids = rows.Select(r => r.Runner.Id).ToList();
        var jobs = await db.GpuJobs.AsNoTracking()
            .Where(j => j.ClaimedByRunnerId != null && ids.Contains(j.ClaimedByRunnerId.Value)
                        && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .Select(j => new { j.Id, j.ClaimedByRunnerId, j.WallId, WallName = j.Wall.Name, j.Progress, j.Stage })
            .ToListAsync();
        var online = DateTimeOffset.UtcNow - queue.Options.OnlineWindow;
        return rows.OrderBy(r => r.Runner.RevokedAt != null).ThenBy(r => r.Runner.CreatedAt).Select(r =>
        {
            var x = r.Runner;
            var job = jobs.FirstOrDefault(j => j.ClaimedByRunnerId == x.Id);
            var sameWall = wallId is null || job?.WallId == wallId || job is null || x.OwnerUserId == userId;
            return new GpuRunnerInfo(
                x.Id, x.Name, x.OwnerUserId, string.IsNullOrWhiteSpace(r.Owner) ? "Unknown" : r.Owner, x.OwnerUserId == userId,
                x.SharedWithOtherWalls, wallId is { } w && r.Walls.Contains(w), x.RevokedAt is null && x.LastSeenAt >= online,
                x.RevokedAt is not null, x.KeyPrefix, x.CreatedAt, x.LastSeenAt, x.LastJobAt,
                new GpuRunnerCapabilities(x.GpuName, x.VramMb, x.MaxQuality, x.MemoryBudgetMb, x.RunnerVersion, x.Platform),
                job is null ? null : new GpuRunnerCurrentJob(job.Id, job.WallId, sameWall ? job.WallName : null, job.Progress, job.Stage),
                r.Walls.Count,
                r.Approved);
        }).ToList();
    }
}
