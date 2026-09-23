using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One solved glyph wall geometry (the full <c>wall-geometry.json</c>, see
/// <c>tools/glyph/wall-geometry.schema.md</c>) for a wall. A wall keeps every model it ever had as
/// history; at most one per wall is <see cref="IsActive"/>, enforced by a filtered unique index.
/// Experimental and additive: only consulted when <see cref="Wall.GlyphsEnabled"/> is set.
/// </summary>
public class WallGeometryModel
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The full wall-geometry.json document, stored verbatim (lengths in mm, angles in degrees).</summary>
    [Required]
    public required string Json { get; set; }

    /// <summary>The document's own <c>version</c> field, so readers can refuse a schema they do not know.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>What produced the model, e.g. "glyph-solver v1".</summary>
    [Required]
    [MaxLength(64)]
    public required string Source { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    /// <summary>The model the app uses for this wall. At most one active model per wall.</summary>
    public bool IsActive { get; set; }

    /// <summary>Summary: overall wall width in millimetres, when the solve reports one.</summary>
    public double? WidthMm { get; set; }

    /// <summary>Summary: overall wall height in millimetres, when the solve reports one.</summary>
    public double? HeightMm { get; set; }

    /// <summary>Summary: the solve's overall reprojection RMS, in pixels.</summary>
    public double? ReprojRmsPx { get; set; }

    [MaxLength(2048)]
    public string? Notes { get; set; }
}
