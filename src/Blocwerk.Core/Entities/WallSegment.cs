using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A sub-area of a wall with its own inclination, so a wall built from several planes
/// (slab, vertical, overhang) can be projected per region instead of as one surface.
/// </summary>
public class WallSegment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public required string Name { get; set; }

    /// <summary>
    /// Inclination in degrees, 0 (vertical, no foreshortening) to 90 (horizontal roof/floor).
    /// This is the tilt of the panel away from vertical about a horizontal axis and squashes
    /// the panel's vertical extent by cos(Angle) — the original single-scalar behaviour.
    /// </summary>
    public int Angle { get; set; }

    /// <summary>
    /// Yaw in degrees, the panel's rotation about the vertical axis relative to a panel that faces the
    /// viewer square-on. 0 (default) faces the viewer like the main wall. Range -90..90; ±90 is edge-on.
    /// <para>
    /// Sign, stated in the viewer's frame: positive = the surface is turned so it faces toward the
    /// viewer's RIGHT, i.e. it is a side surface on the viewer's LEFT (its right edge recedes, its left
    /// edge comes toward the viewer); negative = it faces the viewer's left, i.e. it sits on the viewer's
    /// right. This is the same sense as the glyph solver's <c>frame.yaw_rel</c> and
    /// <see cref="MarkerPlanning.SurfaceYaw"/> (counter-clockwise seen from above), so values cross
    /// between them unchanged. Worked example: The Attic's left side triangle, on the viewer's left,
    /// measured +89° in the solver frame and is +89 here.
    /// </para>
    /// <para>
    /// Derived from the (since removed) 2D schematic projection's maths, not its old labels: it mapped
    /// the panel's axes as u → (cos yaw, 0), v → (−sin yaw · sin incl, cos incl), which is the 3D
    /// rotation u = (cos yaw, 0, +sin yaw) with depth growing away from the viewer and an overhang's
    /// lower edge receding — so a positive yaw sends the right edge away and turns the face right. The
    /// removed facing toggle labelled positive "Side (right)"; that label contradicted the maths
    /// (which, for a vertical panel, never depended on the sign at all).
    /// </para>
    /// </summary>
    public int Yaw { get; set; }

    /// <summary>
    /// Whether this segment is a climbable plane or the floor. A <see cref="WallSegmentKind.Floor"/>
    /// segment is dropped from the folded schematic, marking the region that is not on the wall.
    /// </summary>
    public WallSegmentKind Kind { get; set; } = WallSegmentKind.Wall;

    /// <summary>
    /// Absolute normalized (0..1) polygon vertices, same convention as
    /// <see cref="Wall.BorderPoints"/>.
    /// </summary>
    public List<ShapePoint> Points { get; set; } = [];

    public int SortOrder { get; set; }

    /// <summary>
    /// Binds this row to the glyph marker scheme's physical segment (the solved segment index: the marker plan's, or <c>markerId / 6</c>). Null when
    /// the segment is not (yet) tied to markers. Independent of <see cref="SortOrder"/>.
    /// </summary>
    public int? MarkerSegmentIndex { get; set; }

    /// <summary>
    /// Tilt from vertical in degrees as MEASURED by the glyph solver. Deliberately separate from
    /// the admin-declared <see cref="Angle"/>, which it never overwrites. Null when not measured.
    /// </summary>
    public double? MeasuredAngle { get; set; }

    /// <summary>
    /// Measured yaw in degrees relative to the solver's reference surface (marker segment 0), copied
    /// unchanged from the solver's <c>yawDeg</c>. Positive = the surface faces toward the viewer's right,
    /// i.e. it is on the viewer's left (The Attic's left triangle: +89). Same sense as <see cref="Yaw"/>,
    /// so no conversion is applied. Null when not measured.
    /// </summary>
    public double? MeasuredYaw { get; set; }
}
