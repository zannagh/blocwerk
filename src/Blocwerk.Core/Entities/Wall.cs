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

    /// <summary>
    /// Display-resolution wall photo in the wall's default projection
    /// (<see cref="PhotoProjection"/>). Unchanged in meaning since before stitching existed:
    /// every renderer keeps reading this one.
    /// </summary>
    public byte[]? Photo { get; set; }

    [MaxLength(64)]
    public string? PhotoContentType { get; set; }

    /// <summary>
    /// Display-resolution copy of the SAME wall in the other projection, or null when only one
    /// projection exists. Swapping <see cref="Photo"/> and this is a pure image swap: hold
    /// coordinates are normalised per axis, so the vertical scale between the two cancels out.
    /// </summary>
    public byte[]? PhotoAlternate { get; set; }

    [MaxLength(64)]
    public string? PhotoAlternateContentType { get; set; }

    /// <summary>Which projection <see cref="Photo"/> currently is; the alternate is the other one.</summary>
    public WallPhotoProjection PhotoProjection { get; set; } = WallPhotoProjection.Angled;

    /// <summary>Stored file name of the full-resolution ortho master (see <c>IWallPhotoMasterStorage</c>).</summary>
    [MaxLength(512)]
    public string? OrthoMasterPath { get; set; }

    /// <summary>Stored file name of the full-resolution angled master (see <c>IWallPhotoMasterStorage</c>).</summary>
    [MaxLength(512)]
    public string? AngledMasterPath { get; set; }

    /// <summary>Wall inclination the projection pair was rendered with, in degrees. Null pre-stitching.</summary>
    public double? PhotoWallAngleDegrees { get; set; }

    /// <summary>
    /// Vertical scale applied to get the angled projection from the ortho one, i.e.
    /// <c>cos(PhotoWallAngleDegrees)</c>. Kept so the pair can be reproduced or verified.
    /// </summary>
    public double? PhotoVerticalScale { get; set; }

    /// <summary>Staged counterpart of <see cref="Photo"/>, pending confirmation.</summary>
    public byte[]? StagedPhoto { get; set; }

    [MaxLength(64)]
    public string? StagedPhotoContentType { get; set; }

    /// <summary>Staged counterpart of <see cref="PhotoAlternate"/>.</summary>
    public byte[]? StagedPhotoAlternate { get; set; }

    [MaxLength(64)]
    public string? StagedPhotoAlternateContentType { get; set; }

    /// <summary>Which projection <see cref="StagedPhoto"/> is.</summary>
    public WallPhotoProjection StagedPhotoProjection { get; set; } = WallPhotoProjection.Angled;

    /// <summary>Staged counterpart of <see cref="OrthoMasterPath"/>.</summary>
    [MaxLength(512)]
    public string? StagedOrthoMasterPath { get; set; }

    /// <summary>Staged counterpart of <see cref="AngledMasterPath"/>.</summary>
    [MaxLength(512)]
    public string? StagedAngledMasterPath { get; set; }

    /// <summary>Staged counterpart of <see cref="PhotoWallAngleDegrees"/>.</summary>
    public double? StagedPhotoWallAngleDegrees { get; set; }

    /// <summary>Staged counterpart of <see cref="PhotoVerticalScale"/>.</summary>
    public double? StagedPhotoVerticalScale { get; set; }

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

    public ICollection<WallMember> Members { get; set; } = [];

    public ICollection<Hold> Holds { get; set; } = [];

    public ICollection<Boulder> Boulders { get; set; } = [];

    public ICollection<WallReset> Resets { get; set; } = [];

    /// <summary>
    /// Sub-areas of the wall with their own inclination. Empty means the wall is a single
    /// plane and <see cref="Angle"/> plus <see cref="BorderPoints"/> describe it.
    /// </summary>
    public ICollection<WallSegment> Segments { get; set; } = [];
}
