// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>
/// A runner as the wall panel and the site-admin list show it. Never carries the key. <c>IsPaused</c>: online and paused
/// by its owner (its last hello said so); an offline runner is never "paused", offline wins.
/// </summary>
public sealed record GpuRunnerInfo(
    Guid Id,
    string Name,
    Guid OwnerUserId,
    string OwnerName,
    bool IsMine,
    bool SharedWithOtherWalls,
    bool ServesThisWall,
    bool IsOnline,
    bool IsRevoked,
    string KeyPrefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastJobAt,
    GpuRunnerCapabilities Capabilities,
    GpuRunnerCurrentJob? CurrentJob,
    int WallCount,
    bool ApprovedForThisWall = false,
    bool IsPaused = false);

/// <summary>What the runner reported about itself in <c>hello</c>.</summary>
public sealed record GpuRunnerCapabilities(
    string? GpuName, int? VramMb, string? MaxQuality, int? MemoryBudgetMb, string? RunnerVersion, string? Platform,
    string? Capabilities = null, int? TexturesMemoryMb = null);

/// <summary>
/// The job a runner holds right now. <c>OtherWall</c>: it belongs to a wall the viewer does not administer, so only the
/// fact that the runner is busy is shown (no job id, wall, progress or stage).
/// </summary>
public sealed record GpuRunnerCurrentJob(Guid JobId, Guid WallId, string? WallName, double Progress, string? Stage, bool OtherWall = false)
{
    /// <summary>A job of another wall, as a viewer who may not see it is told about it.</summary>
    public static GpuRunnerCurrentJob Busy { get; } = new(Guid.Empty, Guid.Empty, null, 0, null, OtherWall: true);
}

/// <summary>A freshly created runner and its key, shown exactly once.</summary>
public sealed record GpuRunnerCreated(GpuRunnerInfo Runner, string Key);

/// <summary>One of the owner's walls, and whether the runner serves it.</summary>
public sealed record GpuRunnerWallChoice(Guid WallId, string Name, bool Serves);

/// <summary>A waiting or running GPU job of a wall, for the wall panel.</summary>
public sealed record GpuJobInfo(
    Guid Id, Guid CaptureId, string Quality, string Status, double Progress, string? Stage, int Attempts,
    DateTimeOffset CreatedAt, string? RunnerName, string Kind = "splat");
