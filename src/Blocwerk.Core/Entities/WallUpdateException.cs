// <copyright file="WallUpdateException.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One card of the "Update panels + 3D" confirm screen: a hold of a <see cref="WallUpdateSession"/> whose evidence
/// needs a person's look (see <see cref="UpdateExceptionKind"/>). Written together with the quick review's default
/// decisions, in the same transaction, and replaced whole whenever those are written again. A card never changes what
/// is promoted by itself: an answer is recorded as the decision a person would make in the full review, and an
/// unanswered card about an old hold only marks that hold for review when it goes live.
/// <para>
/// Like the other decision rows this is ephemeral working state: real FKs that CASCADE from the session and from both
/// hold ends, so a staged hold deleted mid-session takes its card with it. Not change-journalled.
/// </para>
/// </summary>
public class WallUpdateException
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    public UpdateExceptionKind Kind { get; set; }

    /// <summary>The old live hold (possibly removed, low-confidence match).</summary>
    public Guid? OldHoldId { get; set; }

    /// <summary>The staged hold (low-confidence match: the twin; conflicting evidence: the detection).</summary>
    public Guid? StagedHoldId { get; set; }

    /// <summary>The staged panel the card's new-photo spot is on.</summary>
    public Guid? PanelId { get; set; }

    /// <summary>The spot on the staged panel photo, normalised (possibly removed: where the old hold should be).</summary>
    public double? X { get; set; }

    /// <summary>The spot on the staged panel photo, normalised.</summary>
    public double? Y { get; set; }

    /// <summary>The 3D model the texture spot is on.</summary>
    public Guid? GeometryModelId { get; set; }

    /// <summary>The facet of <see cref="GeometryModelId"/> the spot is on.</summary>
    [MaxLength(128)]
    public string? FacetId { get; set; }

    /// <summary>Plane a of the spot, mm.</summary>
    public double? A { get; set; }

    /// <summary>Plane b of the spot, mm.</summary>
    public double? B { get; set; }

    /// <summary>Old photo against the new photo at the spot (normalised cross-correlation).</summary>
    public double? PhotoScore { get; set; }

    /// <summary>Old photo against the 3D texture at the spot (normalised cross-correlation).</summary>
    public double? TextureScore { get; set; }

    /// <summary>The matcher's confidence (low-confidence match).</summary>
    public double? Confidence { get; set; }

    public UpdateExceptionStatus Status { get; set; } = UpdateExceptionStatus.Pending;

    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
