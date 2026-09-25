// <copyright file="HoldProposal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A hold found in several capture photos that matches no existing hold (<see cref="Geometry.Proposals.HoldProposalFinder"/>),
/// waiting for a wall admin. Never a hold by itself: accepting creates one on <see cref="PanelId"/> through the normal
/// hold-creation path (panel truth); rejecting is remembered, so the spot is not proposed again. A new run replaces
/// only the PENDING proposals.
/// </summary>
public class HoldProposal
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The geometry model it was found on (a plain id: models are history).</summary>
    public Guid GeometryModelId { get; set; }

    [Required]
    [MaxLength(32)]
    public required string FacetId { get; set; }

    /// <summary>Its point in the facet's frame, mm (<see cref="H"/> above the plane: a volume or its own relief).</summary>
    public double A { get; set; }

    public double B { get; set; }

    public double H { get; set; }

    /// <summary>Its point in the wall world, mm (for "near an earlier proposal" checks).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double SizeMm { get; set; }

    /// <summary>Capture photos that see it.</summary>
    public int Views { get; set; }

    public double Confidence { get; set; }

    /// <summary>The clearest view for the review crop: camera name and box centre / half size, px.</summary>
    [Required]
    [MaxLength(64)]
    public required string BestPhoto { get; set; }

    public double BestPx { get; set; }

    public double BestPy { get; set; }

    public double BestRadiusPx { get; set; }

    /// <summary>The panel photo it maps into (normalised position and radius); null: seen in 3D only, not on any panel photo.</summary>
    public Guid? PanelId { get; set; }

    public double? PanelX { get; set; }

    public double? PanelY { get; set; }

    public double? PanelRadius { get; set; }

    public HoldProposalStatus Status { get; set; }

    /// <summary>The hold an accepted proposal became.</summary>
    public Guid? HoldId { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
