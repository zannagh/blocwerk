using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class Wall
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(256)]
    public required string Name { get; set; }

    [MaxLength(1024)]
    public string? Description { get; set; }

    public byte[]? Photo { get; set; }

    [MaxLength(64)]
    public string? PhotoContentType { get; set; }

    public byte[]? StagedPhoto { get; set; }

    [MaxLength(64)]
    public string? StagedPhotoContentType { get; set; }

    public DateTimeOffset? StagedAt { get; set; }

    public Guid? StagedByUserId { get; set; }

    public WallStagingMode StagingMode { get; set; }

    public Guid OwnerId { get; set; }

    [ForeignKey(nameof(OwnerId))]
    public User Owner { get; set; } = null!;

    [MaxLength(64)]
    public string? ShareToken { get; set; }

    public int Angle { get; set; }

    public List<ShapePoint>? BorderPoints { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastResetAt { get; set; }

    public int CurrentGeneration { get; set; }

    /// <summary>
    /// When true the wall is a "big wall" made of multiple images arranged on a grid (see
    /// <see cref="Panels"/>). When false (the default) the wall is a single image held on
    /// <see cref="Photo"/> and behaves exactly as before.
    /// </summary>
    public bool UsesMultipleImages { get; set; }

    /// <summary>
    /// When true the wall is in "update mode": everyone except the admin who enabled it (see
    /// <see cref="MaintenanceByUserId"/>) sees a "this wall is currently being updated" notice
    /// instead of the wall, so an in-progress re-shoot/update is hidden from members until done.
    /// </summary>
    public bool UnderMaintenance { get; set; }

    /// <summary>The user who put the wall into update mode; they still see the real wall.</summary>
    public Guid? MaintenanceByUserId { get; set; }

    public ICollection<WallMember> Members { get; set; } = [];

    public ICollection<Hold> Holds { get; set; } = [];

    public ICollection<Boulder> Boulders { get; set; } = [];

    public ICollection<WallReset> Resets { get; set; } = [];

    /// <summary>
    /// Sub-areas of the wall with their own inclination. Empty means the wall is a single
    /// plane and <see cref="Angle"/> plus <see cref="BorderPoints"/> describe it.
    /// </summary>
    public ICollection<WallSegment> Segments { get; set; } = [];

    /// <summary>
    /// The images making up a big wall (see <see cref="UsesMultipleImages"/>). Empty on a
    /// normal single-image wall.
    /// </summary>
    public ICollection<WallPanel> Panels { get; set; } = [];
}
