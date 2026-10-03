using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A rectified per-facet texture of a <see cref="WallGeometryModel"/>, rendered by the geometry
/// compute service. The image is on disk; <c>A/B</c> bounds are the facet-plane millimetres the
/// image spans (u = a across, v = b up), so a 3D view can map it onto the facet.
/// </summary>
public class WallGeometryTexture
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GeometryModelId { get; set; }

    [ForeignKey(nameof(GeometryModelId))]
    public WallGeometryModel GeometryModel { get; set; } = null!;

    /// <summary>The facet id of the geometry document ("0", "5a", …).</summary>
    [Required]
    [MaxLength(32)]
    public required string FacetId { get; set; }

    /// <summary>Stored name under the capture file store (a bare file name).</summary>
    [Required]
    [MaxLength(128)]
    public required string StoredPath { get; set; }

    [MaxLength(32)]
    public string ContentType { get; set; } = "image/jpeg";

    public long SizeBytes { get; set; }

    /// <summary>
    /// Stored name of the photo-coverage mask (8-bit grayscale PNG, same pixel grid as the image: 0 = no
    /// photo there, 255 = covered). Null for textures made before the worker returned masks: those
    /// render opaque as before.
    /// </summary>
    [MaxLength(128)]
    public string? MaskStoredPath { get; set; }

    public long? MaskSizeBytes { get; set; }

    /// <summary>
    /// Stored name of the source-view map (JSON, <c>docker/wall-geometry/wallgeometry/sourcemap.py</c>): which
    /// capture photo painted each cell of the image, so the 3D view can draw a hold's outline where the
    /// image shows it. Null for textures made before the worker returned it: outlines stay flat as before.
    /// </summary>
    [MaxLength(128)]
    public string? SourceMapStoredPath { get; set; }

    public long? SourceMapSizeBytes { get; set; }

    public double AMin { get; set; }

    public double AMax { get; set; }

    public double BMin { get; set; }

    public double BMax { get; set; }

    public int WidthPx { get; set; }

    public int HeightPx { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
