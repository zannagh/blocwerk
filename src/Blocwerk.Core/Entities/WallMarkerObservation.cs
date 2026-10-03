using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One ArUco marker detected in one panel photo. Scoped to the panel generation (and to the live
/// vs. staged photo) it was detected in, because a wall is routinely a mix of generations.
/// Experimental and additive: only consulted when <see cref="Wall.GlyphsEnabled"/> is set.
/// </summary>
public class WallMarkerObservation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallPanelId { get; set; }

    [ForeignKey(nameof(WallPanelId))]
    public WallPanel WallPanel { get; set; } = null!;

    /// <summary>The panel generation whose photo the marker was detected in.</summary>
    public int PanelGeneration { get; set; }

    /// <summary>True when detected in <see cref="WallPanel.StagedPhoto"/> rather than <see cref="WallPanel.Photo"/>.</summary>
    public bool FromStagedPhoto { get; set; }

    /// <summary>The decoded marker id (the wall's marker plan ids, or <c>segment*6+role</c> 0..35 without a plan).</summary>
    public int MarkerId { get; set; }

    /// <summary>
    /// The four corners as JSON, normalized 0..1 to the photo (same space as <see cref="Hold.X"/>),
    /// in ArUco order TL, TR, BR, BL of the marker's printed orientation.
    /// </summary>
    [Required]
    public required string CornersJson { get; set; }

    /// <summary>Mean apparent side length of the marker in the photo, in pixels.</summary>
    public double SidePx { get; set; }

    /// <summary>True when any corner was reconstructed rather than detected (e.g. cut off by the frame edge).</summary>
    public bool Synthetic { get; set; }

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The marker plan revision the photo SHOWS, inferred from its markers when detected (older rows: the
    /// wall's current revision at detection; null: no plan yet, the legacy ids). A marker changed in another
    /// revision is never mapped with its new pose (see <c>MarkerRevisionScope</c>).
    /// </summary>
    public int? PlanRevision { get; set; }

    /// <summary>
    /// The lowest plan revision the photo's markers are compatible with (null: not inferred, an older tag).
    /// <see cref="PlanRevision"/> is the best-supported revision within
    /// <see cref="CompatibleRevisionFrom"/>..<see cref="CompatibleRevisionTo"/>; a range wider than one means only
    /// markers unchanged across it were visible (see <c>MarkerRevisionInference</c>).
    /// </summary>
    public int? CompatibleRevisionFrom { get; set; }

    /// <summary>The highest plan revision the photo's markers are compatible with (null: not inferred).</summary>
    public int? CompatibleRevisionTo { get; set; }
}
