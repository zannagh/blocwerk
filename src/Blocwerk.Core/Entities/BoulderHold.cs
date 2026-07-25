using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class BoulderHold
{
    public Guid BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder Boulder { get; set; } = null!;

    public Guid HoldId { get; set; }

    [ForeignKey(nameof(HoldId))]
    public Hold Hold { get; set; } = null!;

    public HoldType Type { get; set; } = HoldType.Normal;

    /// <summary>
    /// Whether this hold is usable with hands, feet, or both within this boulder.
    /// Any value other than <see cref="HoldUsage.HandAndFoot"/> implies the boulder
    /// defines its footholds explicitly (see <see cref="Boulder.FootholdMode"/>).
    /// </summary>
    public HoldUsage Usage { get; set; } = HoldUsage.HandAndFoot;
}
