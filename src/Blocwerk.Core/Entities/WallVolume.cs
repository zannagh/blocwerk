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

    /// <summary>
    /// Removed by a wall admin as falsely detected: not drawn, not used, and a re-detection on the model does not create
    /// it again (a candidate overlapping its footprint by IoU ≥ 0.5 is skipped). Kept so it can be restored.
    /// </summary>
    public bool IsRemoved { get; set; }

    /// <summary>When it was removed.</summary>
    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>
    /// "Has flat sides": <see cref="SurfaceJson"/> holds planar faces (<see cref="Geometry.Volumes.FlatSidedFitter"/>) and the
    /// measured height field waits in <see cref="HeightFieldJson"/> until it is switched off again.
    /// </summary>
    public bool HasFlatSides { get; set; }

    /// <summary>The measured height field while <see cref="HasFlatSides"/> is on; null otherwise.</summary>
    public string? HeightFieldJson { get; set; }

    /// <summary>How far the flat faces are from the measured height field (trimmed RMS), mm; null without flat sides.</summary>
    public double? FlatFitRmsMm { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
