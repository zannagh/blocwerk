// <copyright file="WallMarkerPlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One saved marker plan of a wall (the full plan JSON, see <c>tools/glyph/marker-plan.schema.md</c>).
/// Every save that changes the plan adds a row (the next <see cref="Revision"/>); the newest is <see cref="IsCurrent"/> (at most one per wall, enforced by a
/// filtered unique index) and the older ones stay as history.
/// </summary>
public class WallMarkerPlan
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The plan JSON, stored exactly as <c>MarkerPlanJson.ToJson</c> wrote it.</summary>
    [Required]
    public required string Json { get; set; }

    /// <summary>The plan's <c>schemaVersion</c>, so readers can refuse a schema they do not know.</summary>
    public int SchemaVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    /// <summary>The plan the wall uses now. At most one current plan per wall.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// The plan's revision number within its wall: 1 for the first save, then +1 for every save that
    /// changes the plan. Captures, geometry models and marker observations record the revision they were
    /// made with, so markers that were moved, resized or replaced later are never mistaken for the old ones.
    /// </summary>
    public int Revision { get; set; }
}
