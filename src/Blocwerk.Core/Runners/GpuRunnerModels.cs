// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>A runner as the wall panel and the site-admin list show it. Never carries the key.</summary>
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
    bool ApprovedForThisWall = false);

/// <summary>What the runner reported about itself in <c>hello</c>.</summary>
public sealed record GpuRunnerCapabilities(
    string? GpuName, int? VramMb, string? MaxQuality, int? MemoryBudgetMb, string? RunnerVersion, string? Platform);

/// <summary>The job a runner holds right now.</summary>
public sealed record GpuRunnerCurrentJob(Guid JobId, Guid WallId, string? WallName, double Progress, string? Stage);

/// <summary>A freshly created runner and its key, shown exactly once.</summary>
public sealed record GpuRunnerCreated(GpuRunnerInfo Runner, string Key);

/// <summary>One of the owner's walls, and whether the runner serves it.</summary>
public sealed record GpuRunnerWallChoice(Guid WallId, string Name, bool Serves);

/// <summary>A waiting or running GPU job of a wall, for the wall panel.</summary>
public sealed record GpuJobInfo(
    Guid Id, Guid CaptureId, string Quality, string Status, double Progress, string? Stage, int Attempts,
    DateTimeOffset CreatedAt, string? RunnerName);
