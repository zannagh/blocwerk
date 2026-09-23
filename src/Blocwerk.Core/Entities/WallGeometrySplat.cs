// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The photo-real Gaussian-splat scene of a <see cref="WallGeometryModel"/>, trained by the splat
/// compute worker from the same capture photos. The <c>.spz</c> file is on disk (capture file store);
/// the splat stays in the worker's own (COLMAP) coordinates and <see cref="FrameJson"/> carries the
/// transform into the wall's metric frame, so the 3D view can lay it exactly over the facets.
/// At most one per model; a newer run replaces it.
/// </summary>
public class WallGeometrySplat
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GeometryModelId { get; set; }

    [ForeignKey(nameof(GeometryModelId))]
    public WallGeometryModel GeometryModel { get; set; } = null!;

    /// <summary>Stored name of the <c>.spz</c> under the capture file store (a bare file name).</summary>
    [Required]
    [MaxLength(128)]
    public required string StoredPath { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>The worker's <c>frame.json</c> as returned (matrix, toWorldMm, crop, alignment, stats).</summary>
    [Required]
    public required string FrameJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
