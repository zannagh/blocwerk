// <copyright file="WallEnhancementItem.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>One thing a wall enhancement did or suggests, waiting for (or holding) its review answer.</summary>
public class WallEnhancementItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }

    [ForeignKey(nameof(RunId))]
    public WallEnhancementRun Run { get; set; } = null!;

    public WallEnhancementItemKind Kind { get; set; }

    /// <summary>The hold it is about (the left hold of a pair); null for a new-hold suggestion.</summary>
    public Guid? HoldId { get; set; }

    /// <summary>The other hold: the right hold of a duplicate or link pair, the base hold of a stack.</summary>
    public Guid? OtherHoldId { get; set; }

    /// <summary>The suggestion's own row, for a new hold (the hold proposal).</summary>
    public Guid? ReferenceId { get; set; }

    /// <summary>Kind-specific detail: the suggested colour, the suggested merge mode.</summary>
    [MaxLength(64)]
    public string? Detail { get; set; }

    public WallEnhancementDecision Decision { get; set; } = WallEnhancementDecision.Pending;

    /// <summary>Why an answer could not be carried out, when it could not.</summary>
    [MaxLength(300)]
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Who answered (a plain id).</summary>
    public Guid? DecidedByUserId { get; set; }
}
