using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One run of the "detect real outlines for existing holds" admin action on a wall. It records exactly
/// which holds it changed and what they looked like before (<see cref="HoldIdsJson"/>), so the run can be
/// reverted precisely — and only where nobody has edited the hold since.
/// </summary>
public class HoldOutlineUpgradeRun
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Who ran it. A plain id, not a foreign key, so account deletion never touches the history.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Whether manually placed holds were in scope.</summary>
    public bool IncludedManual { get; set; }

    /// <summary>
    /// The changed holds as a JSON array of <c>HoldOutlineUpgradeEntry</c>: hold id, a hash of what was
    /// written, and the values it replaced. Grows batch by batch in the same save as the holds.
    /// </summary>
    [Required]
    public string HoldIdsJson { get; set; } = "[]";

    /// <summary>Circle holds that were in scope.</summary>
    public int EligibleCount { get; set; }

    /// <summary>Holds that got a real outline.</summary>
    public int OutlinedCount { get; set; }

    /// <summary>Holds that stayed circles.</summary>
    public int KeptCircleCount { get; set; }

    /// <summary>Outlined holds with a pocket / through-hole.</summary>
    public int WithHolesCount { get; set; }

    /// <summary>Holds that got a fingerprint.</summary>
    public int FingerprintedCount { get; set; }

    /// <summary>Holds that got millimetre sizes (marker walls only).</summary>
    public int MeasuredCount { get; set; }

    /// <summary>When the run was reverted, or null.</summary>
    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary>Holds the revert restored.</summary>
    public int RevertedCount { get; set; }
}
