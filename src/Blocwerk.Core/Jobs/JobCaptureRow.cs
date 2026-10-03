// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Jobs;

/// <summary>The columns of a capture the progress API reads.</summary>
internal sealed record JobCaptureRow(
    Guid Id,
    Guid WallId,
    WallCaptureStatus Status,
    double Progress,
    string? Stage,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? UpdatedAt,
    int Attempts,
    string? TimelineJson,
    string? FollowUpJson,
    string? TexturesJobId,
    string? SolveJobId);
