using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class Hold
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>
    /// The big-wall panel this hold belongs to (see <see cref="WallPanel"/>). Null means the
    /// hold sits on the legacy single/center wall photo, which is the case for every normal wall.
    /// </summary>
    public Guid? WallPanelId { get; set; }

    [ForeignKey(nameof(WallPanelId))]
    public WallPanel? WallPanel { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Radius { get; set; } = 0.02;

    public List<ShapePoint>? ShapePoints { get; set; }

    [MaxLength(64)]
    public string? Name { get; set; }

    [MaxLength(32)]
    public string? Color { get; set; }

    /// <summary>
    /// What the hold is made of. Restricts the selectable colors (wooden holds only
    /// come in brown tones). Null on holds created before materials were introduced.
    /// </summary>
    public HoldMaterial? Material { get; set; }

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

    /// <summary>
    /// A detached deep copy of this hold's data (scalars + shape points, no navigation properties).
    /// Used to hand editable working copies to the photo editor: the editor mutates hold
    /// coordinates in place, so without a clone it would corrupt a shared/cached wall aggregate.
    /// </summary>
    public Hold Clone() => new()
    {
        Id = Id,
        WallId = WallId,
        WallPanelId = WallPanelId,
        X = X,
        Y = Y,
        Radius = Radius,
        ShapePoints = ShapePoints?.Select(sp => new ShapePoint { Dx = sp.Dx, Dy = sp.Dy }).ToList(),
        Name = Name,
        Color = Color,
        Material = Material,
        Category = Category,
        IsOnKickboard = IsOnKickboard,
        IsAutoDetected = IsAutoDetected,
        Confidence = Confidence,
        Generation = Generation,
        NeedsReview = NeedsReview,
        IsVirtual = IsVirtual,
        AlignmentSourceHoldId = AlignmentSourceHoldId,
    };
}
