using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One confirmed statement from the neighbour-overlap walk of a <see cref="WallUpdateSession"/>: either
/// a centre ↔ staged-panel correspondence to persist as a <see cref="HoldLink"/> on promote, or a staged
/// panel hold the user marked as physically absent. Separate from <see cref="WallUpdateHoldDecision"/>
/// because it is scoped to a PANEL (which the per-hold verdicts have no use for) and is rewritten whole
/// each time the user re-confirms that panel.
/// <para>CASCADE from both hold ends and from the panel, for the same reason as the hold decisions:
/// ephemeral working state that must not outlive what it refers to.</para>
/// </summary>
public class WallUpdateNeighbourDecision
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    public WallUpdateNeighbourDecisionKind Kind { get; set; }

    /// <summary>The staged non-centre panel this decision belongs to.</summary>
    public Guid PanelId { get; set; }

    [ForeignKey(nameof(PanelId))]
    public WallPanel Panel { get; set; } = null!;

    /// <summary>The hold ON that panel: the link's B end, or the hold marked absent.</summary>
    public Guid HoldId { get; set; }

    [ForeignKey(nameof(HoldId))]
    public Hold Hold { get; set; } = null!;

    /// <summary>
    /// The staged CENTRE hold at the other end of the correspondence (the link's A end). Null for a
    /// <see cref="WallUpdateNeighbourDecisionKind.Removed"/> row.
    /// </summary>
    public Guid? CentreHoldId { get; set; }

    [ForeignKey(nameof(CentreHoldId))]
    public Hold? CentreHold { get; set; }

    /// <summary>Whether the link is recorded as <see cref="HoldLinkKind.Moved"/> rather than Same.</summary>
    public bool Moved { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
