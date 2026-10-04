// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A "3D runner": a GPU machine somewhere (a home PC, a rented GPU box, a Mac) that PULLS the
/// photo-real (Gaussian splat) training work of Blocwerk captures. It connects out with its runner key
/// (<c>bwr_…</c>), claims a queued <see cref="GpuJob"/>, downloads that job's prepared bundle, trains,
/// and uploads the trained splat. Everything that runs on a CPU (COLMAP, alignment, crop, export, the
/// level-of-detail ladder) stays on the server.
/// </summary>
/// <remarks>
/// Only the SHA-256 of the key is stored (as for <see cref="ApiKey"/>), so the key exists in full
/// exactly once, in the answer to the call that created it. A runner serves the walls listed in
/// <see cref="GpuRunnerWall"/> (walls its owner administers) and, when a site admin set
/// <see cref="SharedWithOtherWalls"/>, other walls whose admin approved THIS runner (<see cref="GpuRunnerApproval"/>)
/// and that have no runner of their own online. A runner whose owner is deleted, the Ghost or locked out does nothing.
/// </remarks>
public class GpuRunner
{
    /// <summary>Marks a bearer value as a runner key (never valid as an API key or a JWT).</summary>
    public const string TokenPrefix = "bwr_";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public required string Name { get; set; }

    /// <summary>The user who created the runner; only they (or a site admin) manage it.</summary>
    public Guid OwnerUserId { get; set; }

    [ForeignKey(nameof(OwnerUserId))]
    public User Owner { get; set; } = null!;

    /// <summary>
    /// "Other walls can use this runner" (set by site admins only): walls whose admin approved this runner and that have
    /// no runner of their own online may use it.
    /// </summary>
    public bool SharedWithOtherWalls { get; set; }

    /// <summary>Hex SHA-256 of the full key. The key itself is never stored.</summary>
    [Required]
    [MaxLength(128)]
    public required string KeyHash { get; set; }

    /// <summary>The displayable leading characters of the key.</summary>
    [Required]
    [MaxLength(16)]
    public required string KeyPrefix { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last authenticated call of the runner; "online" means within the last minute.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>When the runner last claimed a job.</summary>
    public DateTimeOffset? LastJobAt { get; set; }

    /// <summary>Set on revoke; the key stops working with the next request.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Reported by the runner (<c>hello</c>): the GPU's name.</summary>
    [MaxLength(200)]
    public string? GpuName { get; set; }

    /// <summary>Reported by the runner: GPU memory (unified memory on Apple Silicon), MB.</summary>
    public int? VramMb { get; set; }

    /// <summary>Reported by the runner: the highest quality profile its memory budget fits.</summary>
    [MaxLength(16)]
    public string? MaxQuality { get; set; }

    /// <summary>Reported by the runner: its memory budget for training, MB.</summary>
    public int? MemoryBudgetMb { get; set; }

    /// <summary>Reported by the runner: its trainer (<c>gsplat</c> on CUDA, <c>brush</c> on Vulkan/Metal).</summary>
    [MaxLength(32)]
    public string? Trainer { get; set; }

    /// <summary>
    /// Reported by the runner (<c>hello</c>): what it can do, comma separated (<c>splat,textures</c>). Null for a runner that
    /// reports none (older versions): it trains splats only and is never offered a textures job.
    /// </summary>
    [MaxLength(64)]
    public string? Capabilities { get; set; }

    /// <summary>Reported by the runner: the memory a textures job may use on it, MB (null: it renders none).</summary>
    public int? TexturesMemoryMb { get; set; }

    /// <summary>Reported by the runner: its software version.</summary>
    [MaxLength(64)]
    public string? RunnerVersion { get; set; }

    /// <summary>Reported by the runner: OS / architecture / trainer version, for the admin list.</summary>
    [MaxLength(200)]
    public string? Platform { get; set; }

    /// <summary>
    /// Reported by the runner (<c>hello</c>): its owner paused it, so it is up but takes no new jobs. Null for a runner that
    /// does not report it (older versions). A paused runner keeps saying hello, so it stays "online" for the overview, but
    /// the queue treats it as unavailable (no jobs are routed or left to it). A claim clears it.
    /// </summary>
    public bool? Paused { get; set; }

    /// <summary>
    /// Not stored: the claim token of the runner process making the current call (<c>X-Blocwerk-Claim</c>), null for a
    /// runner that sends none. Two processes with the same key have different tokens; see <see cref="GpuJob.ClaimToken"/>.
    /// </summary>
    [NotMapped]
    public string? ClaimToken { get; set; }
}
