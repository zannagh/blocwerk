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
    /// Yaw in degrees, the panel's rotation about the vertical axis, i.e. how far it is turned
    /// to face sideways instead of the camera. 0 (default) means the panel faces the camera
    /// exactly like the main wall, so an all-default segment projects exactly as before.
    /// Positive turns the panel's right edge away from the viewer (a side wall on the right),
    /// negative turns its left edge away. Range -90..90; a magnitude of 90 is edge-on.
    /// </summary>
    public int Yaw { get; set; }

    /// <summary>
    /// Absolute normalized (0..1) polygon vertices, same convention as
    /// <see cref="Wall.BorderPoints"/>.
    /// </summary>
    public List<ShapePoint> Points { get; set; } = [];

    public int SortOrder { get; set; }
}
