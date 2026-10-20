// <copyright file="WallEnhancementRun.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The one-time enhancement of a wall generation ("Enhance this wall"): every enhancing step in one background run,
/// a review of what it did, and the record that it was finalized. At most one row per wall and generation (a unique
/// index), so a generation is never enhanced twice; a wall update bumps <see cref="Wall.CurrentGeneration"/> and so
/// enables it again. Working state, not wall content: deliberately not journalled.
/// </summary>
public class WallEnhancementRun
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The wall's <see cref="Wall.CurrentGeneration"/> the run enhances.</summary>
    public int Generation { get; set; }

    /// <summary>Each live panel's generation at the start, as a JSON object (panel id to generation).</summary>
    [Required]
    public string PanelGenerationsJson { get; set; } = "{}";

    public WallEnhancementStatus Status { get; set; } = WallEnhancementStatus.Running;

    /// <summary>The key of the step that is running, or null.</summary>
    [MaxLength(64)]
    public string? CurrentStep { get; set; }

    /// <summary>What each step did, as a JSON array of <c>WallEnhancementStepRecord</c>; saved as each step starts and ends.</summary>
    [Required]
    public string StepsJson { get; set; } = "[]";

    /// <summary>Who started it. A plain id, not a foreign key, so account deletion never touches the history.</summary>
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the last step finished and the review opened.</summary>
    public DateTimeOffset? StepsFinishedAt { get; set; }

    /// <summary>When an admin finalized the review: from then on the generation counts as enhanced.</summary>
    public DateTimeOffset? FinalizedAt { get; set; }

    /// <summary>Who finalized it (a plain id).</summary>
    public Guid? FinalizedByUserId { get; set; }

    /// <summary>The finalized outcome (per kind kept / undone / skipped, and each step's summary) as JSON.</summary>
    public string? OutcomeJson { get; set; }

    /// <summary>Why the run stopped, when it did.</summary>
    [MaxLength(500)]
    public string? Error { get; set; }
}
