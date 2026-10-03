using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One run of "place existing holds on the 3D model" on a wall: its panel photos registered onto the active
/// model's facet textures, and each hold's facet position written. It records exactly which holds it placed
/// and their metric fields before (<see cref="HoldsJson"/>), so the run can be reverted precisely — and only
/// where nobody has moved the hold since.
/// </summary>
public class HoldPlacementRun
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The geometry model whose textures the photos were registered onto. A plain id: models are history.</summary>
    public Guid GeometryModelId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Who ran it (for a capture: the admin who started the capture). A plain id, not a foreign key.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>What started it: "admin" (wall settings), "api" or "capture" (after a capture's model went live).</summary>
    [Required]
    [MaxLength(16)]
    public string Trigger { get; set; } = "admin";

    /// <summary>
    /// The placed holds as a JSON array of <c>HoldPlacementEntry</c>: hold id, a hash of the placement written,
    /// and the values it replaced. Grows batch by batch in the same save as the holds.
    /// </summary>
    [Required]
    public string HoldsJson { get; set; } = "[]";

    /// <summary>Per panel photo: placed / skipped / failed and each facet's registration evidence (JSON).</summary>
    [Required]
    public string PanelsJson { get; set; } = "[]";

    /// <summary>Holds that got a facet position.</summary>
    public int PlacedCount { get; set; }

    /// <summary>Holds left alone: already placed by markers or an edit, virtual, or without a panel photo.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Holds in scope that no registered facet contained.</summary>
    public int FailedCount { get; set; }

    /// <summary>When the run was reverted, or null.</summary>
    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary>Holds the revert restored.</summary>
    public int RevertedCount { get; set; }
}
