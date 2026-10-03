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
    /// DEPRECATED — effectively always <c>true</c>. Every wall is now a "big wall" made of one or more
    /// images on a grid (see <see cref="Panels"/>), with a center (0,0) panel seeded from
    /// <see cref="Photo"/> by the upload path and the startup converge
    /// (<c>WallCenterPanelConvergence</c>). The single-image branch has been removed and no code should
    /// branch on this flag; it is kept only so the column can be dropped in a later, separately-tested
    /// migration. The one remaining writer converges it to <c>true</c>.
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

    /// <summary>
    /// When true, a registered kiosk tablet bolted to THIS wall may create a boulder with nobody
    /// signed in. <b>Default false, and deliberately opt-in per wall</b> — it is the only write in
    /// the app an unauthenticated caller can reach, so it must be a decision a wall admin made on
    /// purpose for one gym, not a capability every kiosk wall silently acquires.
    /// </summary>
    /// <remarks>
    /// This flag alone authorises nothing. It is the last of four conditions, all of which must
    /// hold: a valid kiosk device cookie, whose key still validates against the database, whose wall
    /// is exactly this wall, and this flag. See <c>KioskAnonymousSetting</c>.
    /// </remarks>
    public bool AllowAnonymousKioskSetting { get; set; }

    /// <summary>
    /// When true, the kiosk tablet bolted to THIS wall may act on keyboard shortcuts.
    /// <b>Default false, and deliberately opt-in per wall</b> — a kiosk is a shared, unattended
    /// tablet, so any passer-by (or a stray bluetooth keyboard left on the bench) could otherwise
    /// drive destructive editor shortcuts without ever touching the screen.
    /// </summary>
    /// <remarks>
    /// Only a wall admin, from their own device, can turn this on: a kiosk session must never be
    /// able to grant itself the capability. See <c>IWallService.SetKioskKeyboardShortcutsAsync</c>.
    /// </remarks>
    public bool AllowKioskKeyboardShortcuts { get; set; }

    /// <summary>
    /// The <see cref="CurrentGeneration"/> at which an editor declared cross-panel hold linking done,
    /// which hides the "link holds across panels" callout on the wall page. Null means never declared.
    /// </summary>
    /// <remarks>
    /// <b>Storing the generation rather than a bool is the whole point: it SELF-INVALIDATES.</b> A
    /// promote bumps <see cref="CurrentGeneration"/>, so the stored value falls behind and the wall
    /// reads as not finalized again without the promote path knowing this flag exists. Only equality
    /// counts as finalized — a value greater than the current generation (a rolled-back or stale
    /// write) must not silently keep the callout hidden. Events that invalidate the links WITHIN a
    /// generation (a panel confirmed live, a panel's holds re-detected) reset it to null explicitly.
    /// Linking or unlinking individual holds deliberately does NOT reset it: that happens inside the
    /// link tool, where the editor can see the state and switch the reminder back on by hand.
    /// </remarks>
    public int? LinksFinalizedGeneration { get; set; }

    /// <summary>
    /// Opt-in switch for the experimental glyph (ArUco marker) wall geometry. <b>Default false.</b>
    /// While false, every glyph-derived value (marker observations, geometry models, metric hold
    /// fields) is ignored and the normalized per-panel pipeline behaves exactly as before.
    /// </summary>
    public bool GlyphsEnabled { get; set; }

    /// <summary>
    /// The printed marker size for this wall — the side of the black square, in millimetres. Null
    /// when unknown. Sheets of several sizes share ids, so the scale can never be inferred from an id.
    /// </summary>
    public double? MarkerSizeMm { get; set; }

    /// <summary>
    /// "Volumes on this wall have flat sides" (default off): newly detected volumes get flat faces automatically when
    /// they fit well (<see cref="WallVolume.HasFlatSides"/>); each volume can still be switched back.
    /// </summary>
    public bool VolumesHaveFlatSides { get; set; }

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
