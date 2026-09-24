// <copyright file="WallVolume.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A volume found on a facet of a <see cref="WallGeometryModel"/> WITHOUT markers, from the capture's 3D
/// evidence (<see cref="Geometry.Volumes.VolumeDetector"/>). Derived data, additive: it never changes panels,
/// holds' panel positions or boulders. Its shape is a height field over the parent facet
/// (<see cref="Geometry.Volumes.VolumeSurface"/>); holds whose photo ray meets it get a
/// <see cref="Hold.VolumePlacementJson"/>. A wall admin can hide a wrong one (<see cref="IsHidden"/>); a new
/// detection run on the same model replaces the rows but keeps the hidden flags of volumes found again.
/// </summary>
public class WallVolume
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The wall (a plain id for queries; the model is the owner).</summary>
    public Guid WallId { get; set; }

    public Guid GeometryModelId { get; set; }

    [ForeignKey(nameof(GeometryModelId))]
    public WallGeometryModel GeometryModel { get; set; } = null!;

    /// <summary>The facet it stands on (the model's facet id).</summary>
    [Required]
    [MaxLength(32)]
    public required string FacetId { get; set; }

    /// <summary>Stable order within the model (by facet, then position), for names like "Volume 3".</summary>
    public int Index { get; set; }

    /// <summary>Convex outline on the facet as JSON <c>[[a, b], …]</c> in mm.</summary>
    [Required]
    public required string FootprintJson { get; set; }

    /// <summary>The shape as <see cref="Geometry.Volumes.VolumeSurfaceDocument"/> JSON.</summary>
    [Required]
    public required string SurfaceJson { get; set; }

    public double AreaM2 { get; set; }

    /// <summary>90th-percentile height above the wall, mm.</summary>
    public double HeightMm { get; set; }

    /// <summary>0–1 plausibility of the detection.</summary>
    public double Confidence { get; set; }

    /// <summary>Holds placed onto it by the last run.</summary>
    public int HoldCount { get; set; }

    /// <summary>What it was found from: "splat" (the photo-real scene's surface points).</summary>
    [Required]
    [MaxLength(32)]
    public string Source { get; set; } = "splat";

    /// <summary>Hidden by a wall admin: not drawn and not used to place holds.</summary>
    public bool IsHidden { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
