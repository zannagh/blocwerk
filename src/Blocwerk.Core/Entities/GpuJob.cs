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

    /// <summary>
    /// The runners whose training of this job failed (JSON array of runner ids, see <c>GpuJobFailedRunners</c>). A runner
    /// in it does not get the job again while another runner that may train it is online.
    /// </summary>
    [MaxLength(1000)]
    public string? FailedRunnerIdsJson { get; set; }

    /// <summary>
    /// The highest training step a runner had a resumable checkpoint at when it handed the job back on a shutdown. A
    /// shutdown whose checkpoint got further than this is free: the next claim does not start over.
    /// </summary>
    public int? CheckpointStep { get; set; }

    /// <summary>
    /// When the claiming runner last spoke for the job (claim, progress). A claim silent for a while may be taken over
    /// by the same runner again (it restarted) instead of waiting for the lease to run out.
    /// </summary>
    public DateTimeOffset? HeartbeatAt { get; set; }

    /// <summary>
    /// The claim token of the runner process holding the job (null: a runner that sends none). Job calls from another
    /// process of the same runner key are refused; a re-attach hands the job (and the token) to the restarted process.
    /// </summary>
    [MaxLength(64)]
    public string? ClaimToken { get; set; }

    /// <summary>How often the holding runner re-attached to this job after a restart; past a cap, each one costs a lost lease.</summary>
    public int ReattachCount { get; set; }

    /// <summary>Budget-free hand-backs (pauses, shutdowns that kept progress); past a cap they count as shutdowns.</summary>
    public int PauseCount { get; set; }

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

    /// <summary>
    /// The newest preview (the splats after <see cref="PreviewStep"/> steps) a runner uploaded while training, while it
    /// waits to be installed; null once installed (then <see cref="InstalledPreviewPath"/>) or dropped.
    /// </summary>
    [MaxLength(200)]
    public string? PreviewPath { get; set; }

    /// <summary>
    /// The model's view (a <see cref="WallGeometrySplat"/> id, null = none) when the pending preview arrived: it installs
    /// only over that view, never over one installed since (a newer view of any origin wins).
    /// </summary>
    public Guid? PreviewBaseSplatId { get; set; }

    /// <summary>The uploaded splat of the preview installed on the model (<see cref="PreviewInstalledStep"/>).</summary>
    [MaxLength(200)]
    public string? InstalledPreviewPath { get; set; }

    /// <summary>
    /// While a re-finish of this job runs: the job's and its capture's state before it (JSON), restored when the re-finish
    /// fails, so the view that is still installed keeps its status.
    /// </summary>
    public string? RefinishStateJson { get; set; }

    public long? PreviewBytes { get; set; }

    /// <summary><c>ply</c> or <c>spz</c> (content-checked on upload).</summary>
    [MaxLength(8)]
    public string? PreviewFormat { get; set; }

    /// <summary>The training step of <see cref="PreviewPath"/>; a later preview must have a higher one.</summary>
    public int? PreviewStep { get; set; }

    /// <summary>The training's total steps, as the runner reports them with a preview.</summary>
    public int? TotalSteps { get; set; }

    /// <summary>The step of the preview installed on the model (null: none); only ever grows.</summary>
    public int? PreviewInstalledStep { get; set; }

    /// <summary>The CPU worker's finish job of the pending preview, so a restart resumes it.</summary>
    [MaxLength(100)]
    public string? PreviewFinishJobId { get; set; }

    [MaxLength(2048)]
    public string? Error { get; set; }
}
