// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A wall admin's consent that ONE specific shared 3D runner (<see cref="GpuRunner.SharedWithOtherWalls"/>, set by a
/// site admin) may train this wall's photo-real view. The photos then leave for a machine someone else owns, so the
/// consent names the machine: a runner shared later needs its own approval. It counts only while the approving user
/// still administers the wall (see <c>GpuJobQueue.Approvals</c>).
/// </summary>
public class GpuRunnerApproval
{
    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public Guid RunnerId { get; set; }

    [ForeignKey(nameof(RunnerId))]
    public GpuRunner Runner { get; set; } = null!;

    /// <summary>The wall admin who approved the runner.</summary>
    public Guid ApprovedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
