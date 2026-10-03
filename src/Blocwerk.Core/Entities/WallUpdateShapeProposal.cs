// <copyright file="WallUpdateShapeProposal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One recognised outline for one STAGED hold of a <see cref="WallUpdateSession"/>, plus the reviewer's
/// verdict on it. Working state like the other decision rows: nothing here touches the hold until the
/// promote applies <see cref="Decision"/>, and the row CASCADEs away with the session or the staged hold.
/// Not change-journalled.
/// <para>
/// Shapes are stored as JSON text in the <see cref="Hold.ShapePoints"/> convention (offsets as fractions
/// of the image width/height) relative to (<see cref="AnchorX"/>, <see cref="AnchorY"/>) — the hold centre
/// at recognition time — so the promote can rebase them should the centre have moved since.
/// </para>
/// </summary>
public class WallUpdateShapeProposal
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    /// <summary>The staged (gen N+1) hold the outline is for.</summary>
    public Guid HoldId { get; set; }

    [ForeignKey(nameof(HoldId))]
    public Hold Hold { get; set; } = null!;

    /// <summary>The staged panel the hold sits on (its photo is what was outlined).</summary>
    public Guid PanelId { get; set; }

    public ShapeProposalReason Reason { get; set; }

    public HoldOutlineMethod Method { get; set; }

    /// <summary>0..1, the outliner's own trust in the outline. Circle fallbacks stay at or below 0.2.</summary>
    public double Confidence { get; set; }

    public double AnchorX { get; set; }

    public double AnchorY { get; set; }

    /// <summary>Decoded photo width in pixels, so a review crop can be drawn without distortion.</summary>
    public int ImageWidth { get; set; }

    /// <summary>Decoded photo height in pixels.</summary>
    public int ImageHeight { get; set; }

    /// <summary>The recognised outline, or null for a circle fallback (nothing trustworthy found).</summary>
    public string? ShapeJson { get; set; }

    /// <summary>The recognised interior holes (pocket/donut), or null.</summary>
    public string? HolesJson { get; set; }

    /// <summary>
    /// The shape the hold would promote with if this step did nothing: its own staged outline, or the
    /// carried old hold's outline. Null when that is a plain circle. Drives "use the old shape".
    /// </summary>
    public string? PreviousShapeJson { get; set; }

    public ShapeReviewDecision Decision { get; set; } = ShapeReviewDecision.Pending;

    /// <summary>The reviewer's hand-adjusted outline, relative to the anchor; only for <see cref="ShapeReviewDecision.Adjusted"/>.</summary>
    public string? AdjustedShapeJson { get; set; }

    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
