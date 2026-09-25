// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A wall admin's consent that OTHER admins' shared 3D runners (<see cref="GpuRunner.SharedWithOtherWalls"/>) may
/// train this wall's photo-real view. The photos then leave for a machine someone else owns, so no row = no
/// shared runner ever sees this wall's jobs (the wall's own runners are unaffected).
/// </summary>
public class GpuRunnerSharedOptIn
{
    [Key]
    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The admin who opted the wall in.</summary>
    public Guid OptedInByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
