// <copyright file="WallLookOverFinding.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// Something an applied panel update was unsure about and nobody answered before it went live: kept with the wall so a
/// wall admin or moderator can still look it over afterwards ("Look over the wall"). Written by the promote, in its
/// transaction, as a copy of what the confirm screen's card showed (<see cref="WallUpdateException"/> rows die with
/// their staged hold) plus what only exists after the promote: the live hold the old one became.
/// <para>
/// The hold and panel ids are plain ids without foreign keys, so a hold deleted later (by an answer here, or in the
/// editor) never blocks anything; a finding whose hold is gone can only be settled. Not change-journalled.
/// </para>
/// </summary>
public class WallLookOverFinding
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The update session that was promoted, when there was one.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>The wall generation the update produced.</summary>
    public int Generation { get; set; }

    public LookOverFindingKind Kind { get; set; }

    /// <summary>The hold before the update (a retained row of the previous generation; its panel keeps the old photo).</summary>
    public Guid? OldHoldId { get; set; }

    /// <summary>The live hold the finding is about: what the old hold became. Null for a detection that was left out.</summary>
    public Guid? HoldId { get; set; }

    /// <summary>The new hold on the same spot, when there is one (replaced, same spot).</summary>
    public Guid? OtherHoldId { get; set; }

    /// <summary>The panel of the new generation the spot is on.</summary>
    public Guid? PanelId { get; set; }

    /// <summary>The spot on that panel's photo, normalised.</summary>
    public double X { get; set; }

    /// <summary>The spot on that panel's photo, normalised.</summary>
    public double Y { get; set; }

    /// <summary>The radius to show around the spot, normalised to the photo's long side.</summary>
    public double Radius { get; set; }

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

    /// <summary>Old photo against the 3D texture at the spot.</summary>
    public double? TextureScore { get; set; }

    /// <summary>The matcher's confidence (unsure match).</summary>
    public double? Confidence { get; set; }

    /// <summary>Boulders that used the hold when the update was applied.</summary>
    public int BouldersAtApply { get; set; }

    /// <summary>
    /// The update marked <see cref="HoldId"/> for review because of this finding alone (it was not marked before), so
    /// settling the finding may clear the mark.
    /// </summary>
    public bool FlaggedHold { get; set; }

    /// <summary>
    /// The boulders on <see cref="HoldId"/> that this update marked for review and that were not marked before it: the
    /// only boulders an answer saying "the update was wrong about this hold" may clear again. Null on a finding written
    /// before this was recorded (unknown, so nothing is cleared).
    /// </summary>
    public List<Guid>? MarkedBoulderIds { get; set; }

    /// <summary>The answer took the review mark off <see cref="HoldId"/> (undoing the answer puts it back).</summary>
    public bool ClearedHoldMark { get; set; }

    /// <summary>The boulders whose review mark the answer cleared (undoing the answer marks them again).</summary>
    public List<Guid>? ClearedBoulderIds { get; set; }

    /// <summary>How many current boulders used the hold when it was answered (shown with a removal or a merge).</summary>
    public int? AnswerBoulders { get; set; }

    /// <summary>An unsure match a person called a different hold; it stays open as a replaced hold.</summary>
    public bool Converted { get; set; }

    public LookOverFindingStatus Status { get; set; } = LookOverFindingStatus.Open;

    /// <summary>The answer that settled it.</summary>
    public LookOverAnswer? Answer { get; set; }

    public Guid? AnsweredByUserId { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>The change-journal batch of the merge, when the answer was <see cref="LookOverAnswer.UseNewHold"/> (undo with it).</summary>
    public Guid? MergeBatchId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
