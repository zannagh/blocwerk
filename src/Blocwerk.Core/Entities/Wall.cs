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
    /// projection exists. NOT interchangeable with <see cref="Photo"/>: the two projections are
    /// different geometries, so an overlay drawn for one has to be mapped for the other — see
    /// <see cref="WallPhotoProjection"/>.
    /// </summary>
    public byte[]? PhotoAlternate { get; set; }

    [MaxLength(64)]
    public string? PhotoAlternateContentType { get; set; }

    /// <summary>Which projection <see cref="Photo"/> currently is; the alternate is the other one.</summary>
    public WallPhotoProjection PhotoProjection { get; set; } = WallPhotoProjection.Natural;

    /// <summary>Stored file name of the full-resolution flat master (see <c>IWallPhotoMasterStorage</c>).</summary>
    [MaxLength(512)]
    public string? FlatMasterPath { get; set; }

    /// <summary>Stored file name of the full-resolution natural master (see <c>IWallPhotoMasterStorage</c>).</summary>
    [MaxLength(512)]
    public string? NaturalMasterPath { get; set; }

    /// <summary>Physical wall width in metres the pipeline rendered against. Null pre-stitching.</summary>
    public double? PhotoWallWidthM { get; set; }

    /// <summary>Physical wall height in metres the pipeline rendered against. Null pre-stitching.</summary>
    public double? PhotoWallHeightM { get; set; }

    /// <summary>
    /// The pipeline's <c>curvature</c> block verbatim: which projection made the natural master and
    /// the per-variant <c>thetaMaxDeg</c>/<c>k</c>/<c>radiusM</c> behind it. Together with
    /// <see cref="CamerasJson"/> this is what lets a flat-space hold be drawn on the natural view,
    /// so it is stored whole rather than reduced to a single number. Null pre-stitching.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? PhotoCurvatureJson { get; set; }

    /// <summary>
    /// The pipeline's <c>cameras.json</c>: per-frame registration plus the flat-to-natural map
    /// parameters. Opaque to the domain — stored verbatim so renderers and a re-run of the
    /// pipeline can reproduce the mapping. Null pre-stitching.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? CamerasJson { get; set; }

    /// <summary>Staged counterpart of <see cref="Photo"/>, pending confirmation.</summary>
    public byte[]? StagedPhoto { get; set; }

    [MaxLength(64)]
    public string? StagedPhotoContentType { get; set; }

    /// <summary>Staged counterpart of <see cref="PhotoAlternate"/>.</summary>
    public byte[]? StagedPhotoAlternate { get; set; }

    [MaxLength(64)]
    public string? StagedPhotoAlternateContentType { get; set; }

    /// <summary>Which projection <see cref="StagedPhoto"/> is.</summary>
    public WallPhotoProjection StagedPhotoProjection { get; set; } = WallPhotoProjection.Natural;

    /// <summary>Staged counterpart of <see cref="FlatMasterPath"/>.</summary>
    [MaxLength(512)]
    public string? StagedFlatMasterPath { get; set; }

    /// <summary>Staged counterpart of <see cref="NaturalMasterPath"/>.</summary>
    [MaxLength(512)]
    public string? StagedNaturalMasterPath { get; set; }

    /// <summary>Staged counterpart of <see cref="PhotoWallWidthM"/>.</summary>
    public double? StagedPhotoWallWidthM { get; set; }

    /// <summary>Staged counterpart of <see cref="PhotoWallHeightM"/>.</summary>
    public double? StagedPhotoWallHeightM { get; set; }

    /// <summary>Staged counterpart of <see cref="PhotoCurvatureJson"/>.</summary>
    [Column(TypeName = "jsonb")]
    public string? StagedPhotoCurvatureJson { get; set; }

    /// <summary>Staged counterpart of <see cref="CamerasJson"/>.</summary>
    [Column(TypeName = "jsonb")]
    public string? StagedCamerasJson { get; set; }

    /// <summary>
    /// The pipeline's standing caveat about the accuracy of the staged carryover, empty when it did
    /// not run. While this is set the staged result must never be promoted to live unattended: a
    /// human has to look at the overlay and confirm. See <c>WallService.ConfirmStagedPhotoAsync</c>.
    /// </summary>
    [MaxLength(1024)]
    public string? StagedCarryoverBlocker { get; set; }

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
