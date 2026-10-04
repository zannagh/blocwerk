// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// Paused runners (their owner switched them to pause; their last hello said so): a running training whose runner is
/// paused, and a queued one whose wall's online runners are all paused, are marked <see cref="JobProgressItem.RunnerPaused"/>.
/// Uses the queue's own eligibility (<c>GpuJobQueue.Assignments</c>/<c>Approvals</c>), so "serves the wall" means the same.
/// </summary>
public sealed partial class JobProgressReader
{
    private async Task<List<JobProgressItem>> MarkPausedAsync(
        BlocwerkDbContext db, List<JobProgressItem> items, JobProgressReadContext context, CancellationToken ct)
    {
        var training = items.Where(i => JobKinds.IsGpu(i.Kind) && JobStates.IsActive(i.State)).ToList();
        if (runnerQueue is null || training.Count == 0)
        {
            return items;
        }

        var online = context.Now - runnerQueue.Options.OnlineWindow;
        var running = training.Where(i => i.State == JobStates.Running).Select(i => i.GpuJobId!.Value).ToList();
        var pausedJobs = (await db.GpuJobs.AsNoTracking()
                .Where(j => running.Contains(j.Id) && j.ClaimedByRunner != null && j.ClaimedByRunner.Paused == true
                            && j.ClaimedByRunner.LastSeenAt >= online)
                .Select(j => j.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var waitingWalls = training.Where(i => i.State == JobStates.Queued).Select(i => i.WallId).Distinct().ToList();
        var pausedWalls = await WallsWithOnlyPausedRunnersAsync(db, waitingWalls, online, ct);
        return items.Select(i => !JobKinds.IsGpu(i.Kind) || !JobStates.IsActive(i.State) ? i
            : i with { RunnerPaused = i.State == JobStates.Running ? pausedJobs.Contains(i.GpuJobId!.Value) : pausedWalls.Contains(i.WallId) })
            .ToList();
    }

    /// <summary>The walls whose online runners (own or approved shared ones) are all paused; a wall with none is not one.</summary>
    private async Task<HashSet<Guid>> WallsWithOnlyPausedRunnersAsync(
        BlocwerkDbContext db, List<Guid> walls, DateTimeOffset online, CancellationToken ct)
    {
        if (walls.Count == 0)
        {
            return [];
        }

        var own = await runnerQueue!.Assignments(db)
            .Where(rw => walls.Contains(rw.WallId) && rw.Runner.LastSeenAt >= online)
            .Select(rw => new { rw.WallId, Paused = rw.Runner.Paused == true })
            .ToListAsync(ct);
        var shared = await runnerQueue.Approvals(db)
            .Where(a => walls.Contains(a.WallId) && a.Runner.LastSeenAt >= online)
            .Select(a => new { a.WallId, Paused = a.Runner.Paused == true })
            .ToListAsync(ct);
        return own.Concat(shared).GroupBy(r => r.WallId).Where(g => g.All(r => r.Paused)).Select(g => g.Key).ToHashSet();
    }
}
