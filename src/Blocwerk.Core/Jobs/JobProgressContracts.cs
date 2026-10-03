// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Jobs;

/// <summary>The trainer's numbers of a GPU training job.</summary>
/// <param name="Loss">The latest loss, when the trainer prints one.</param>
/// <param name="Splats">The latest splat count, when the trainer prints one.</param>
/// <param name="PreviewStep">The step of the preview waiting to be installed.</param>
/// <param name="PreviewInstalledStep">The step of the preview installed on the model.</param>
/// <param name="StepsPerSecond">The step rate of the current claim, when known.</param>
public sealed record JobTrainingFacts(
    [property: JsonPropertyName("loss")] double? Loss,
    [property: JsonPropertyName("splats")] int? Splats,
    [property: JsonPropertyName("previewStep")] int? PreviewStep,
    [property: JsonPropertyName("previewInstalledStep")] int? PreviewInstalledStep,
    [property: JsonPropertyName("stepsPerSecond")] double? StepsPerSecond);

/// <summary>One stage of a capture's timeline.</summary>
/// <param name="Stage">The stage key.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended (null: running).</param>
/// <param name="Outcome"><c>done</c> or <c>failed</c> once ended.</param>
/// <param name="Seconds">How long it ran (so far, while running).</param>
public sealed record JobStageSpan(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("endedAt")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("seconds")] double Seconds);

/// <summary>The response of the progress API: running jobs and those that ended within the window.</summary>
/// <param name="GeneratedAt">When it was read.</param>
/// <param name="RecentHours">How far back ended jobs are listed.</param>
/// <param name="Jobs">Running jobs first, then the recently ended ones, newest first.</param>
public sealed record JobProgressSnapshot(
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("recentHours")] double RecentHours,
    [property: JsonPropertyName("jobs")] IReadOnlyList<JobProgressItem> Jobs);

/// <summary>What to read.</summary>
/// <param name="WallIds">The walls whose jobs are read; null for every wall (app administrators, telemetry).</param>
/// <param name="Recent">How far back ended jobs are listed (zero: running ones only).</param>
public sealed record JobProgressScope(IReadOnlySet<Guid>? WallIds, TimeSpan Recent)
{
    /// <summary>Every wall's running jobs only (what the telemetry gauges read).</summary>
    public static JobProgressScope RunningEverywhere { get; } = new(null, TimeSpan.Zero);

    /// <summary>Whether <paramref name="wallId"/> is in scope.</summary>
    public bool Covers(Guid wallId) => WallIds is null || WallIds.Contains(wallId);
}
