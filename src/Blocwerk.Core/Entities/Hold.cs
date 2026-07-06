using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class Hold
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public double X { get; set; }

    public double Y { get; set; }

    public double Radius { get; set; } = 0.02;

    public List<ShapePoint>? ShapePoints { get; set; }

    [MaxLength(64)]
    public string? Name { get; set; }

    [MaxLength(32)]
    public string? Color { get; set; }

    public HoldCategory Category { get; set; } = HoldCategory.Hand;

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

    public ICollection<BoulderHold> BoulderHolds { get; set; } = [];
}
