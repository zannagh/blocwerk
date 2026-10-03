// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// One long-running job as the progress API reports it. The JSON names are a contract for scripts: add fields, never
/// rename or remove one. Every value is read or derived from stored facts at request time; null means unknown.
/// </summary>
public sealed record JobProgressItem
{
    /// <summary>Stable id: <c>{kind}:{guid}</c>, or <c>followUp:{captureId}:{step}</c> for a follow-up step.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>One of <see cref="JobKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>One of <see cref="JobStates"/>.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>The machine key of the stage it is in (e.g. <c>solving</c>, <c>training</c>, <c>place-holds</c>).</summary>
    [JsonPropertyName("stage")]
    public string? Stage { get; init; }

    /// <summary>What it is doing, in plain words (the pipeline's own stage text).</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>0..100, or null when the job reports no progress.</summary>
    [JsonPropertyName("percent")]
    public double? Percent { get; init; }

    [JsonPropertyName("step")]
    public int? Step { get; init; }

    [JsonPropertyName("totalSteps")]
    public int? TotalSteps { get; init; }

    /// <summary>Seconds until the current stage ends, or null when unknown (never guessed).</summary>
    [JsonPropertyName("etaSeconds")]
    public double? EtaSeconds { get; init; }

    /// <summary>How <see cref="EtaSeconds"/> was derived: one of <see cref="JobEtaSources"/>, or null.</summary>
    [JsonPropertyName("etaSource")]
    public string? EtaSource { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the job last reported or changed.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("endedAt")]
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>The newest error the job recorded (it may still retry), or null.</summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }

    [JsonPropertyName("wallId")]
    public Guid WallId { get; init; }

    [JsonPropertyName("wallName")]
    public string? WallName { get; init; }

    [JsonPropertyName("captureId")]
    public Guid? CaptureId { get; init; }

    [JsonPropertyName("gpuJobId")]
    public Guid? GpuJobId { get; init; }

    [JsonPropertyName("runnerName")]
    public string? RunnerName { get; init; }

    /// <summary>
    /// GPU training only: true when its runner is paused (running), or when every online runner that may train it is
    /// paused (queued); false otherwise; null for other kinds and ended jobs.
    /// </summary>
    [JsonPropertyName("runnerPaused")]
    public bool? RunnerPaused { get; init; }

    /// <summary>How often it was picked up (captures, GPU jobs), or null.</summary>
    [JsonPropertyName("attempts")]
    public int? Attempts { get; init; }

    /// <summary>The trainer's numbers (GPU training only).</summary>
    [JsonPropertyName("training")]
    public JobTrainingFacts? Training { get; init; }

    /// <summary>The capture's stage timeline (captures only), oldest first.</summary>
    [JsonPropertyName("stages")]
    public IReadOnlyList<JobStageSpan>? Stages { get; init; }
}
