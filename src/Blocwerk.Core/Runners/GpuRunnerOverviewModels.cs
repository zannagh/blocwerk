// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Jobs;

namespace Blocwerk.Core.Runners;

/// <summary>The runner overview as one viewer may see it.</summary>
/// <param name="GeneratedAt">When it was read.</param>
/// <param name="IsAppAdmin">The viewer administers the installation (sees every runner and may share and revoke any).</param>
/// <param name="Runners">Online runners first, then paused, offline and revoked ones.</param>
public sealed record GpuRunnerOverview(DateTimeOffset GeneratedAt, bool IsAppAdmin, IReadOnlyList<GpuRunnerOverviewRow> Runners);

/// <summary>One runner in the overview.</summary>
/// <param name="Id">The runner.</param>
/// <param name="Name">Its name.</param>
/// <param name="OwnerName">Its owner's display name.</param>
/// <param name="IsMine">The viewer owns it.</param>
/// <param name="SharedWithOtherWalls">A site admin offered it to other walls.</param>
/// <param name="State">One of <see cref="GpuRunnerStates"/> (offline wins over paused).</param>
/// <param name="LastSeenAt">Its last call (hello, claim, progress).</param>
/// <param name="LastJobAt">When it last took a job.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="Capabilities">What it reported in its hello.</param>
/// <param name="Walls">The walls it serves (own or approved) that the viewer may see.</param>
/// <param name="OtherWallCount">The walls it serves that the viewer may not see.</param>
/// <param name="Current">What it is doing now, or null when idle.</param>
/// <param name="Failures">Its recent failed trainings.</param>
/// <param name="CanRevoke">The viewer may revoke it (its owner or a site admin).</param>
/// <param name="CanShare">The viewer may offer it to other walls (a site admin).</param>
public sealed record GpuRunnerOverviewRow(
    Guid Id,
    string Name,
    string OwnerName,
    bool IsMine,
    bool SharedWithOtherWalls,
    string State,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastJobAt,
    DateTimeOffset CreatedAt,
    GpuRunnerCapabilities Capabilities,
    IReadOnlyList<GpuRunnerWallRef> Walls,
    int OtherWallCount,
    GpuRunnerActivity? Current,
    GpuRunnerFailures Failures,
    bool CanRevoke,
    bool CanShare);

/// <summary>A wall a runner serves.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Name">Its name.</param>
/// <param name="Approved">Served as a shared runner the wall approved (not as one of its own).</param>
public sealed record GpuRunnerWallRef(Guid WallId, string? Name, bool Approved);

/// <summary>
/// The job a runner holds. <paramref name="OtherWall"/>: a wall the viewer may not see, so nothing else is told (the job's
/// fields are all null).
/// </summary>
/// <param name="OtherWall">Busy with another wall's job.</param>
/// <param name="Job">The job as the progress API shows it (stage, step, ETA, …).</param>
/// <param name="LeaseExpiresAt">Until when its claim holds without a heartbeat.</param>
/// <param name="HeartbeatAt">When the runner last reported on the job.</param>
public sealed record GpuRunnerActivity(bool OtherWall, JobProgressItem? Job, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? HeartbeatAt)
{
    /// <summary>Busy with a job of a wall the viewer may not see.</summary>
    public static GpuRunnerActivity Busy { get; } = new(true, null, null, null);
}

/// <summary>A runner's recent failed trainings (jobs whose failed-runner list names it).</summary>
/// <param name="Count">How many of the recent jobs it failed.</param>
/// <param name="LastAt">When the newest of them last changed.</param>
/// <param name="LastReason">That job's error, or a neutral text when it is another wall's.</param>
public sealed record GpuRunnerFailures(int Count, DateTimeOffset? LastAt, string? LastReason)
{
    /// <summary>No failures.</summary>
    public static GpuRunnerFailures None { get; } = new(0, null, null);
}

/// <summary>The <see cref="GpuRunnerOverviewRow.State"/> values.</summary>
public static class GpuRunnerStates
{
    public const string Online = "online";

    /// <summary>Online, but its owner paused it: it takes no new jobs.</summary>
    public const string Paused = "paused";

    public const string Offline = "offline";

    public const string Revoked = "revoked";

    /// <summary>The state of a runner: revoked, else offline when not seen within the window (offline wins), else paused or online.</summary>
    public static string Of(DateTimeOffset? revokedAt, DateTimeOffset? lastSeenAt, bool? paused, DateTimeOffset onlineSince) =>
        revokedAt is not null ? Revoked
        : !(lastSeenAt >= onlineSince) ? Offline
        : paused is true ? Paused
        : Online;
}
