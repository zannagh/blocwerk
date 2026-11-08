// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One hold-recognition job (runner protocol 1.2): a runner envelope of photos and a request in the capture store, claimed
/// by the instance runner or an external one under a lease, answered with an inline JSON result. Unlike a
/// <see cref="GpuJob"/> it has no capture: an interactive job serves a person waiting in a page. Its status uses the same
/// states as a GPU job. See <c>RecognitionQueue</c>.
/// </summary>
public class RecognitionJob
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The kind's wire name, which is also the capability a runner must advertise (<c>detect-holds@2</c>, <c>markers</c>, …).</summary>
    [MaxLength(32)]
    public required string Kind { get; set; }

    public RecognitionLane Lane { get; set; }

    /// <summary>The wall the photos belong to: only runners that may serve it see them.</summary>
    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public GpuJobStatus Status { get; set; } = GpuJobStatus.Queued;

    /// <summary>The input envelope (zip) in the capture store; null once the job is over or the sweep dropped it.</summary>
    [MaxLength(200)]
    public string? InputPath { get; set; }

    public long InputBytes { get; set; }

    [MaxLength(64)]
    public required string InputSha256 { get; set; }

    /// <summary>Memory the job needs on the runner, MB; a runner whose hello <c>memoryBudgetMb</c> is lower is not offered it.</summary>
    public int? RequiredMemoryMb { get; set; }

    /// <summary>The lease a claim gets and every heartbeat renews (per lane), as the claim's <c>leaseSeconds</c>.</summary>
    public int LeaseSeconds { get; set; }

    public Guid? ClaimedByRunnerId { get; set; }

    [ForeignKey(nameof(ClaimedByRunnerId))]
    public GpuRunner? ClaimedByRunner { get; set; }

    /// <summary>The claim token of the runner process holding the job (see <see cref="GpuJob.ClaimToken"/>).</summary>
    [MaxLength(64)]
    public string? ClaimToken { get; set; }

    /// <summary>
    /// Until when the claiming runner holds the job; renewed by every heartbeat. For a queued interactive job: until when a
    /// runner must claim it before it is cancelled (the claim deadline). Null for a queued background job.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>How long a claim may run, heartbeats or not (per kind and lane, set at enqueue).</summary>
    public int RunSeconds { get; set; }

    /// <summary>Until when the current claim may run (claim time + <see cref="RunSeconds"/>; a re-attach keeps it). Null while queued.</summary>
    public DateTimeOffset? RunDeadlineAt { get; set; }

    /// <summary>How many times a runner claimed it; the job fails once its lane's attempt budget is used up.</summary>
    public int Attempts { get; set; }

    /// <summary>The runners that failed it (JSON array of ids, see <c>GpuJobFailedRunners</c>); they get it again only when no other may.</summary>
    [MaxLength(1000)]
    public string? FailedRunnerIdsJson { get; set; }

    /// <summary>Progress 0..1 as the runner reports it.</summary>
    public double Progress { get; set; }

    [MaxLength(200)]
    public string? Stage { get; set; }

    /// <summary>The kind's result document (status.json's <c>result</c>), once it succeeded.</summary>
    public string? ResultJson { get; set; }

    [MaxLength(2048)]
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>What the caller uses to find its job again (a background job after a restart); free text, not unique.</summary>
    [MaxLength(200)]
    public string? CallerKey { get; set; }
}
