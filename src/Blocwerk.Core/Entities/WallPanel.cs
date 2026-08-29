using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One image of a "big wall" placed on a sparse 2D integer grid. The center panel sits at
/// <see cref="Col"/> 0, <see cref="Row"/> 0 and neighbours grow left/right/up/down. Holds are
/// stored per-panel in normalized 0..1 coordinates. Single-image walls have no panels and keep
/// their image on <see cref="Wall.Photo"/> as before.
/// </summary>
public class WallPanel
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>Grid column; 0 is the center panel, negative to the left, positive to the right.</summary>
    public int Col { get; set; }

    /// <summary>Grid row; 0 is the center panel, negative upward, positive downward.</summary>
    public int Row { get; set; }

    public byte[]? Photo { get; set; }

    [MaxLength(64)]
    public string? PhotoContentType { get; set; }

    public byte[]? StagedPhoto { get; set; }

    [MaxLength(64)]
    public string? StagedPhotoContentType { get; set; }

    public DateTimeOffset? StagedAt { get; set; }

    public Guid? StagedByUserId { get; set; }

    public int Generation { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
