using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One hold on a wall photo: normalized position/shape per panel image, appearance, and (glyph walls)
/// optional metric measurements — see <c>Hold.Glyph.cs</c> for when those go stale.
/// </summary>
public partial class Hold
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>
    /// The big-wall panel this hold belongs to (see <see cref="WallPanel"/>). Null means the
    /// hold sits on the legacy single/center wall photo, which is the case for every normal wall.
    /// </summary>
    public Guid? WallPanelId { get; set; }

    [ForeignKey(nameof(WallPanelId))]
    public WallPanel? WallPanel { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Radius { get; set; } = 0.02;

    public List<ShapePoint>? ShapePoints { get; set; }

    /// <summary>
    /// Interior holes of the outline (a pocket or donut hold): each ring is a list of offsets in the SAME
    /// convention as <see cref="ShapePoints"/> (fractions of the image width/height, relative to
    /// <see cref="X"/>/<see cref="Y"/>). Null (or empty) for a solid hold. Only meaningful together with
    /// <see cref="ShapePoints"/>; a manual reshape of the outer outline drops it
    /// (<see cref="InvalidateGlyphForEdit"/>). Visual only — a tap inside a hole still hits the hold.
    /// </summary>
    public List<List<ShapePoint>>? ShapeHoles { get; set; }

    [MaxLength(64)]
    public string? Name { get; set; }

    [MaxLength(32)]
    public string? Color { get; set; }

    /// <summary>
    /// What the hold is made of. Restricts the selectable colors (wooden holds only
    /// come in brown tones). Null on holds created before materials were introduced.
    /// </summary>
    public HoldMaterial? Material { get; set; }

    public HoldCategory Category { get; set; } = HoldCategory.Hand;

    /// <summary>
    /// The grip sub-type of a hand hold (jug, crimp, sloper, …). Only meaningful when
    /// <see cref="Category"/> is <see cref="HoldCategory.Hand"/>. Null means unspecified.
    /// </summary>
    public HoldHandType? HandType { get; set; }

    public bool IsOnKickboard { get; set; }

    public bool IsAutoDetected { get; set; }

    public double Confidence { get; set; }

    public int Generation { get; set; }

    public bool NeedsReview { get; set; }

    /// <summary>
    /// A hold placed by a user during boulder creation when the physical hold
    /// isn't visible in the current wall photo. Rendered with a dotted outline
    /// and cleared when the hold is merged with a real detected hold during a
    /// wall update.
    /// </summary>
    public bool IsVirtual { get; set; }

    /// <summary>
    /// On a manual-alignment staged clone (generation N+1), the id of the live
    /// hold it was copied from. Null for normal holds and holds added during
    /// alignment. Preserves boulder-hold continuity when the staged set is promoted.
    /// </summary>
    public Guid? AlignmentSourceHoldId { get; set; }

    // ── Glyph wall geometry (experimental, additive) ─────────────────────────────────────────────
    // Everything below is METRIC and optional. None of it reinterprets X/Y/Radius/ShapePoints, which
    // stay normalized 0..1 per panel image. All null until the glyph pipeline measures the hold on a
    // wall with Wall.GlyphsEnabled; consumers must treat null as "not measured", never as zero.

    /// <summary>Measured width of the hold along its facet's u axis, in millimetres.</summary>
    public double? WidthMm { get; set; }

    /// <summary>Measured height of the hold along its facet's v axis, in millimetres.</summary>
    public double? HeightMm { get; set; }

    /// <summary>Measured outline area on the facet plane, in square millimetres.</summary>
    public double? AreaMm2 { get; set; }

    /// <summary>
    /// The wall-geometry facet the hold sits on, as named in wall-geometry.json (e.g. "0", or "5a"
    /// when a segment folds). Qualifies <see cref="PlaneAMm"/>/<see cref="PlaneBMm"/>.
    /// </summary>
    [MaxLength(16)]
    public string? FacetId { get; set; }

    /// <summary>
    /// Hold centre along the facet's u axis (across the surface), in millimetres from the facet
    /// origin. Unlike <see cref="X"/> this is photo-independent, so it is comparable across photos
    /// and generations.
    /// </summary>
    public double? PlaneAMm { get; set; }

    /// <summary>Hold centre along the facet's v axis (up the surface), in millimetres. See <see cref="PlaneAMm"/>.</summary>
    public double? PlaneBMm { get; set; }

    /// <summary>Opaque JSON colour/shape descriptor used to re-recognise the hold across captures.</summary>
    public string? FingerprintJson { get; set; }

    /// <summary>How the hold's outline was produced. Null on holds from before this was recorded.</summary>
    public HoldOutlineSource? OutlineSource { get; set; }

    /// <summary>
    /// 0..1, how far the automatic outline (<see cref="ShapePoints"/>) can be trusted, as the outliner scored
    /// it. Null when the outline was drawn by hand, is a plain circle, or predates the score.
    /// </summary>
    public double? OutlineConfidence { get; set; }

    /// <summary>
    /// How the metric fields were derived, e.g. "multi-marker" (homography from ≥2 markers) or
    /// "single-marker" (exact only near that marker). Null when nothing metric is known.
    /// </summary>
    [MaxLength(32)]
    public string? MetricSource { get; set; }

    /// <summary>
    /// The hold's contact footprint on its facet as JSON (<see cref="Geometry.View3D.HoldFootprint"/>): the
    /// traced silhouette with the protrusion smear removed, from several capture views or a single-view
    /// correction. Written by the batch footprint refinement only; the 3D view prefers it over the
    /// panel-photo projection. Null when never refined or when the outline or position changed since.
    /// </summary>
    public string? FootprintMm { get; set; }

    /// <summary>
    /// How far the hold stands out of its facet as JSON (<see cref="Geometry.View3D.HoldProtrusion"/>): body
    /// height, apex and the surface it sits on (wall or volume), measured from the photo-real scene. Written
    /// after a splat is installed; the 3D view lifts the hold's overlay by it. Null when never measured.
    /// </summary>
    public string? ProtrusionMm { get; set; }

    public ICollection<BoulderHold> BoulderHolds { get; set; } = [];

    /// <summary>
    /// Copies the glyph metric fields (<see cref="WidthMm"/> … <see cref="MetricSource"/>) from
    /// <paramref name="source"/>. For paths that adopt another hold's measured geometry in place.
    /// </summary>
    public void CopyGlyphMetricsFrom(Hold source)
    {
        WidthMm = source.WidthMm;
        HeightMm = source.HeightMm;
        AreaMm2 = source.AreaMm2;
        FacetId = source.FacetId;
        PlaneAMm = source.PlaneAMm;
        PlaneBMm = source.PlaneBMm;
        FingerprintJson = source.FingerprintJson;
        OutlineSource = source.OutlineSource;
        OutlineConfidence = source.OutlineConfidence;
        MetricSource = source.MetricSource;
        FootprintMm = source.FootprintMm;
        ProtrusionMm = source.ProtrusionMm;
    }

    /// <summary>
    /// A detached deep copy of this hold's data (scalars + shape points, no navigation properties).
    /// Used to hand editable working copies to the photo editor: the editor mutates hold
    /// coordinates in place, so without a clone it would corrupt a shared/cached wall aggregate.
    /// </summary>
    public Hold Clone() => new()
    {
        Id = Id,
        WallId = WallId,
        WallPanelId = WallPanelId,
        X = X,
        Y = Y,
        Radius = Radius,
        ShapePoints = ShapePoints?.Select(sp => new ShapePoint { Dx = sp.Dx, Dy = sp.Dy }).ToList(),
        ShapeHoles = ShapePoint.CloneRings(ShapeHoles),
        Name = Name,
        Color = Color,
        Material = Material,
        Category = Category,
        HandType = HandType,
        IsOnKickboard = IsOnKickboard,
        IsAutoDetected = IsAutoDetected,
        Confidence = Confidence,
        Generation = Generation,
        NeedsReview = NeedsReview,
        IsVirtual = IsVirtual,
        AlignmentSourceHoldId = AlignmentSourceHoldId,
        WidthMm = WidthMm,
        HeightMm = HeightMm,
        AreaMm2 = AreaMm2,
        FacetId = FacetId,
        PlaneAMm = PlaneAMm,
        PlaneBMm = PlaneBMm,
        FingerprintJson = FingerprintJson,
        OutlineSource = OutlineSource,
        OutlineConfidence = OutlineConfidence,
        MetricSource = MetricSource,
        FootprintMm = FootprintMm,
        ProtrusionMm = ProtrusionMm,
    };
}
