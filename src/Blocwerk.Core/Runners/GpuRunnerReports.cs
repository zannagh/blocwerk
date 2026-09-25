// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Runners;

/// <summary><c>POST /api/runners/hello</c>: what the runner reports about itself.</summary>
public sealed record RunnerHello(
    [property: JsonPropertyName("runnerVersion")] string? RunnerVersion,
    [property: JsonPropertyName("gpuName")] string? GpuName,
    [property: JsonPropertyName("vramMb")] int? VramMb,
    [property: JsonPropertyName("maxQuality")] string? MaxQuality,
    [property: JsonPropertyName("memoryBudgetMb")] int? MemoryBudgetMb,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("brushVersion")] string? BrushVersion,
    [property: JsonPropertyName("trainer")] string? Trainer = null,
    [property: JsonPropertyName("cuda")] bool? Cuda = null);

/// <summary><c>POST /api/runners/claim</c> (optional body): the quality this runner will train at most right now.</summary>
public sealed record RunnerClaimRequest([property: JsonPropertyName("maxQuality")] string? MaxQuality);

/// <summary><c>POST /api/runners/jobs/{id}/progress</c>.</summary>
public sealed record RunnerProgress(
    [property: JsonPropertyName("fraction")] double? Fraction,
    [property: JsonPropertyName("step")] int? Step,
    [property: JsonPropertyName("totalSteps")] int? TotalSteps,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("detail")] string? Detail);

/// <summary>
/// <c>POST /api/runners/jobs/{id}/fail</c>. <c>Shutdown</c>: the runner is stopping (not the job failing), so the job goes
/// back to the queue without using an attempt. <c>Retryable</c>: another try (maybe on another runner) may succeed.
/// </summary>
public sealed record RunnerFailure(
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("shutdown")] bool Shutdown = false);

/// <summary>A claimed job as the runner receives it.</summary>
public sealed record RunnerClaim(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("quality")] string Quality,
    [property: JsonPropertyName("leaseSeconds")] int LeaseSeconds,
    [property: JsonPropertyName("bundleBytes")] long BundleBytes,
    [property: JsonPropertyName("bundleSha256")] string BundleSha256);

/// <summary>What a job-scoped runner call found.</summary>
public enum RunnerJobOutcome
{
    /// <summary>Done.</summary>
    Ok,

    /// <summary>Not this runner's job (never claimed by it, or unknown): 404.</summary>
    NotYours,

    /// <summary>It was this runner's, but no longer (cancelled, requeued after the lease, finished): 410.</summary>
    Gone,

    /// <summary>The upload is over the size cap: 413.</summary>
    TooLarge,

    /// <summary>The upload is not a splat: 422.</summary>
    Invalid,

    /// <summary>The upload uses a content encoding other than gzip: 415.</summary>
    UnsupportedEncoding,
}
