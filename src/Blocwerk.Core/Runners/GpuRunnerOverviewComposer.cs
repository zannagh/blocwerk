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
    /// wall-panel rule (another wall's job on a shared runner is only "busy"), widened to the site admin. The runner's owner
    /// sees its jobs and the walls it serves, but not another wall's raw errors (<see cref="MaySeeDetails"/>).
    /// </summary>
    public bool MaySeeJob(Guid wallId, Guid runnerOwnerId) => MaySeeDetails(wallId) || runnerOwnerId == UserId;

    /// <summary>Whether the viewer may see that a runner owned by <paramref name="runnerOwnerId"/> serves <paramref name="wallId"/>.</summary>
    public bool MaySeeWall(Guid wallId, Guid runnerOwnerId) => MaySeeJob(wallId, runnerOwnerId);

    /// <summary>Whether the viewer may read the wall's errors as recorded (a site admin, or an admin of the wall).</summary>
    public bool MaySeeDetails(Guid wallId) => IsAppAdmin || Walls.Contains(wallId);
}

/// <summary>A runner as read for the overview.</summary>
/// <param name="Runner">The row.</param>
/// <param name="OwnerName">Its owner's display name.</param>
/// <param name="Walls">Every wall it serves.</param>
public sealed record GpuRunnerOverviewInput(GpuRunner Runner, string OwnerName, IReadOnlyList<GpuRunnerWallRef> Walls);

/// <summary>A job a runner holds now (<paramref name="Running"/>: it reported progress; else it is still claimed).</summary>
public sealed record GpuRunnerHeldJob(
    Guid JobId, Guid RunnerId, Guid WallId, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? HeartbeatAt, bool Running = true, DateTimeOffset? ClaimedAt = null);

/// <summary>A recent training that failed on a runner, with the runner's own reason (<see cref="GpuRunnerFailure"/>).</summary>
public sealed record GpuRunnerFailedJob(Guid RunnerId, Guid WallId, string? WallName, DateTimeOffset At, string? Error);

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
            .Select(r => Row(viewer, r, Held(held, r.Runner.Id), progress, Failures(viewer, r.Runner, failed), onlineSince))
            .OrderBy(r => Rank(r.State))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The job a runner holds; should it hold several (a race), the running one, then the newest claim.</summary>
    internal static GpuRunnerHeldJob? Held(IEnumerable<GpuRunnerHeldJob> held, Guid runnerId) =>
        held.Where(j => j.RunnerId == runnerId)
            .OrderByDescending(j => j.Running)
            .ThenByDescending(j => j.ClaimedAt)
            .ThenBy(j => j.JobId)
            .FirstOrDefault();

    private static GpuRunnerOverviewRow Row(
        GpuRunnerViewer viewer, GpuRunnerOverviewInput input, GpuRunnerHeldJob? job, IReadOnlyDictionary<Guid, JobProgressItem> progress,
        GpuRunnerFailures failures, DateTimeOffset onlineSince)
    {
        var r = input.Runner;
        var visibleWalls = input.Walls.Where(w => viewer.MaySeeWall(w.WallId, r.OwnerUserId)).ToList();
        var current = job is null ? null
            : !viewer.MaySeeJob(job.WallId, r.OwnerUserId) ? GpuRunnerActivity.Busy
            : new GpuRunnerActivity(false, Shown(viewer, job.WallId, progress.GetValueOrDefault(job.JobId)), job.LeaseExpiresAt, job.HeartbeatAt);
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
        return new GpuRunnerFailures(mine.Count, last.At, Reason(viewer, runner, last));
    }

    /// <summary>
    /// The failure's reason as the viewer may read it: as recorded for an admin of its wall; the generic text with the wall's
    /// name for the runner's owner; the generic text alone for anyone else.
    /// </summary>
    internal static string? Reason(GpuRunnerViewer viewer, GpuRunner runner, GpuRunnerFailedJob failure) =>
        viewer.MaySeeDetails(failure.WallId) ? failure.Error
        : viewer.MaySeeJob(failure.WallId, runner.OwnerUserId) ? $"{OtherWallFailure} ({failure.WallName ?? "?"})"
        : OtherWallFailure;

    /// <summary>The held job as the viewer may see it: its own wall's admins see its error, the runner's owner does not.</summary>
    private static JobProgressItem? Shown(GpuRunnerViewer viewer, Guid wallId, JobProgressItem? job) =>
        job is null || viewer.MaySeeDetails(wallId) ? job : job with { LastError = null };

    private static int Rank(string state) => state switch
    {
        GpuRunnerStates.Online => 0,
        GpuRunnerStates.Paused => 1,
        GpuRunnerStates.Offline => 2,
        _ => 3,
    };
}
