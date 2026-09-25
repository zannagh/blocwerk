// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The GPU half of one capture's photo-real view: Brush training on a bundle the server prepared
/// (undistorted, metadata-free images + the COLMAP sparse model + the training options). A
/// <see cref="GpuRunner"/> claims it under a lease, reports progress (which extends the lease) and
/// uploads the trained splat; the server then finishes and installs it. An expired lease puts the
/// job back in the queue. Reported training failures and vanished runners are counted separately
/// (<c>MaxAttempts</c>, <c>MaxLostLeases</c>); a runner that shuts down or is revoked costs nothing.
/// </summary>
public class GpuJob
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public Guid CaptureId { get; set; }

    [ForeignKey(nameof(CaptureId))]
    public WallCapture Capture { get; set; } = null!;

    /// <summary>The geometry model the finished splat is installed on.</summary>
    public Guid GeometryModelId { get; set; }

    public SplatQuality Quality { get; set; } = SplatQuality.High;

    public GpuJobStatus Status { get; set; } = GpuJobStatus.Queued;

    public Guid? ClaimedByRunnerId { get; set; }

    [ForeignKey(nameof(ClaimedByRunnerId))]
    public GpuRunner? ClaimedByRunner { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>
    /// Until when the claiming runner holds the job; extended by every progress report. For a delivered job not yet
    /// installed: when the sweep next hands it back to the capture pipeline (the splat worker was unreachable).
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Training progress 0..1 as the runner reports it.</summary>
    public double Progress { get; set; }

    [MaxLength(200)]
    public string? Stage { get; set; }

    /// <summary>How many times a runner claimed it (information only; the two counters below decide).</summary>
    public int Attempts { get; set; }

    /// <summary>Training failures a runner reported (retryable ones); the job fails at <c>MaxAttempts</c>.</summary>
    public int FailureCount { get; set; }

    /// <summary>Claims lost because the runner vanished (lease expired); the job fails at <c>MaxLostLeases</c>.</summary>
    public int LostLeaseCount { get; set; }

    /// <summary>
    /// Claims given back because the runner shut down. Free up to <c>MaxFreeShutdowns</c>; past that each one costs a
    /// training attempt, so a runner that claims and "shuts down" in a loop cannot hold a job forever.
    /// </summary>
    public int ShutdownCount { get; set; }

    /// <summary>The training bundle (zip) in the capture file store: the ONLY thing a runner ever sees.</summary>
    [Required]
    [MaxLength(200)]
    public required string BundlePath { get; set; }

    public long BundleBytes { get; set; }

    [Required]
    [MaxLength(64)]
    public required string BundleSha256 { get; set; }

    /// <summary>The prepare step's server-only state (alignment inputs, stats) for the finish step.</summary>
    [Required]
    [MaxLength(200)]
    public required string PreparedPath { get; set; }

    /// <summary>The uploaded trained splat (<c>.ply</c> or <c>.spz</c>) in the capture file store.</summary>
    [MaxLength(200)]
    public string? ResultPath { get; set; }

    public long? ResultBytes { get; set; }

    /// <summary><c>ply</c> or <c>spz</c> (content-checked on upload).</summary>
    [MaxLength(8)]
    public string? ResultFormat { get; set; }

    /// <summary>The runner's training stats (JSON object), merged into the splat's stats at finish.</summary>
    public string? ResultStatsJson { get; set; }

    /// <summary>The CPU worker's finish job (crop, cleanup, export), so a restart resumes it.</summary>
    [MaxLength(100)]
    public string? FinishJobId { get; set; }

    /// <summary>When the finished splat was installed on the model.</summary>
    public DateTimeOffset? InstalledAt { get; set; }

    [MaxLength(2048)]
    public string? Error { get; set; }
}
