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

    public ICollection<BoulderHold> BoulderHolds { get; set; } = [];
}
