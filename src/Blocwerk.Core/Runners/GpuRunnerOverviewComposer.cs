// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Jobs;

namespace Blocwerk.Core.Runners;

/// <summary>Who looks at the overview: a site admin sees everything; anyone else their walls and their own runners' jobs.</summary>
/// <param name="UserId">The viewer.</param>
/// <param name="IsAppAdmin">Administers the installation.</param>
/// <param name="Walls">The walls the viewer administers (ignored for a site admin).</param>
public sealed record GpuRunnerViewer(Guid UserId, bool IsAppAdmin, IReadOnlySet<Guid> Walls)
{
    /// <summary>
    /// Whether a job of <paramref name="wallId"/> on a runner owned by <paramref name="runnerOwnerId"/> may be shown: the
    /// wall-panel rule (another wall's job on a shared runner is only "busy"), widened to the site admin.
    /// </summary>
    public bool MaySeeJob(Guid wallId, Guid runnerOwnerId) => IsAppAdmin || Walls.Contains(wallId) || runnerOwnerId == UserId;

    /// <summary>Whether the viewer may see that the runner serves <paramref name="wallId"/>.</summary>
    public bool MaySeeWall(Guid wallId) => IsAppAdmin || Walls.Contains(wallId);
}

/// <summary>A runner as read for the overview.</summary>
/// <param name="Runner">The row.</param>
/// <param name="OwnerName">Its owner's display name.</param>
/// <param name="Walls">Every wall it serves.</param>
public sealed record GpuRunnerOverviewInput(GpuRunner Runner, string OwnerName, IReadOnlyList<GpuRunnerWallRef> Walls);

/// <summary>A job a runner holds now.</summary>
public sealed record GpuRunnerHeldJob(Guid JobId, Guid RunnerId, Guid WallId, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? HeartbeatAt);

/// <summary>A recent job a runner failed.</summary>
public sealed record GpuRunnerFailedJob(Guid RunnerId, Guid WallId, DateTimeOffset At, string? Error);

/// <summary>Composes the overview rows from what was read, applying the viewer's visibility. Pure (the tests' seam).</summary>
public static class GpuRunnerOverviewComposer
{
    /// <summary>What a failure of a wall the viewer may not see says.</summary>
    public const string OtherWallFailure = "a job of another wall";

    public static List<GpuRunnerOverviewRow> Compose(
        GpuRunnerViewer viewer,
        IEnumerable<GpuRunnerOverviewInput> runners,
        IReadOnlyList<GpuRunnerHeldJob> held,
        IReadOnlyDictionary<Guid, JobProgressItem> progress,
        IReadOnlyList<GpuRunnerFailedJob> failed,
        DateTimeOffset onlineSince) =>
        runners
            .Select(r => Row(viewer, r, held.FirstOrDefault(j => j.RunnerId == r.Runner.Id), progress, Failures(viewer, r.Runner, failed), onlineSince))
            .OrderBy(r => Rank(r.State))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static GpuRunnerOverviewRow Row(
        GpuRunnerViewer viewer, GpuRunnerOverviewInput input, GpuRunnerHeldJob? job, IReadOnlyDictionary<Guid, JobProgressItem> progress,
        GpuRunnerFailures failures, DateTimeOffset onlineSince)
    {
        var r = input.Runner;
        var visibleWalls = input.Walls.Where(w => viewer.MaySeeWall(w.WallId)).ToList();
        var current = job is null ? null
            : !viewer.MaySeeJob(job.WallId, r.OwnerUserId) ? GpuRunnerActivity.Busy
            : new GpuRunnerActivity(false, progress.GetValueOrDefault(job.JobId), job.LeaseExpiresAt, job.HeartbeatAt);
        return new GpuRunnerOverviewRow(
            r.Id, r.Name, input.OwnerName, r.OwnerUserId == viewer.UserId, r.SharedWithOtherWalls,
            GpuRunnerStates.Of(r.RevokedAt, r.LastSeenAt, r.Paused, onlineSince), r.LastSeenAt, r.LastJobAt, r.CreatedAt,
            new GpuRunnerCapabilities(r.GpuName, r.VramMb, r.MaxQuality, r.MemoryBudgetMb, r.RunnerVersion, r.Platform),
            visibleWalls, input.Walls.Count - visibleWalls.Count, current, failures,
            CanRevoke: r.RevokedAt is null && (viewer.IsAppAdmin || r.OwnerUserId == viewer.UserId),
            CanShare: r.RevokedAt is null && viewer.IsAppAdmin);
    }

    private static GpuRunnerFailures Failures(GpuRunnerViewer viewer, GpuRunner runner, IReadOnlyList<GpuRunnerFailedJob> failed)
    {
        var mine = failed.Where(f => f.RunnerId == runner.Id).OrderByDescending(f => f.At).ToList();
        if (mine.Count == 0)
        {
            return GpuRunnerFailures.None;
        }

        var last = mine[0];
        var reason = viewer.MaySeeJob(last.WallId, runner.OwnerUserId) ? last.Error : OtherWallFailure;
        return new GpuRunnerFailures(mine.Count, last.At, reason);
    }

    private static int Rank(string state) => state switch
    {
        GpuRunnerStates.Online => 0,
        GpuRunnerStates.Paused => 1,
        GpuRunnerStates.Offline => 2,
        _ => 3,
    };
}
