using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One per-hold verdict in the carryover review of a <see cref="WallUpdateSession"/>, saved the moment
/// the user makes it. Covers BOTH halves of that review, because they have the same shape — one subject
/// hold, at most one paired hold, one verdict — and their subject sets are disjoint by generation:
/// a <see cref="WallUpdateHoldDecisionKind.Carry"/> subject is an old live gen-N hold, a
/// <see cref="WallUpdateHoldDecisionKind.NewCentreHold"/> subject a staged gen-N+1 hold.
/// <para>
/// Real FKs, CASCADE from both hold ends: a staged hold can be deleted mid-session (DeleteStagedHoldAsync),
/// and a decision about a hold that no longer exists must clean itself up rather than dangle. That is safe
/// precisely because these rows are ephemeral working state, not history — unlike <see cref="HoldLink"/>
/// and <see cref="HoldGenerationLink"/>, which are Restrict and are prepared by <see cref="Data.HoldDeletion"/>.
/// </para>
/// </summary>
public class WallUpdateHoldDecision
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    public WallUpdateHoldDecisionKind Kind { get; set; }

    /// <summary>
    /// The hold the decision is ABOUT: the old live hold for <see cref="WallUpdateHoldDecisionKind.Carry"/>,
    /// the staged centre hold for <see cref="WallUpdateHoldDecisionKind.NewCentreHold"/>.
    /// </summary>
    public Guid HoldId { get; set; }

    [ForeignKey(nameof(HoldId))]
    public Hold Hold { get; set; } = null!;

    /// <summary>
    /// For a carry decision, the staged centre hold whose position the old hold takes over. Null for
    /// <see cref="CarryKind.Removed"/> and for every new-centre decision.
    /// </summary>
    public Guid? PairedHoldId { get; set; }

    [ForeignKey(nameof(PairedHoldId))]
    public Hold? PairedHold { get; set; }

    /// <summary>The carry verdict; only meaningful for <see cref="WallUpdateHoldDecisionKind.Carry"/>.</summary>
    public CarryKind CarryKind { get; set; } = CarryKind.Carried;

    /// <summary>
    /// For a new-centre decision, whether the staged hold is discarded (true) or kept as a genuinely new
    /// live hold (false). Ignored for carry decisions, whose verdict is <see cref="CarryKind"/>.
    /// </summary>
    public bool Discarded { get; set; }

    /// <summary>
    /// Whether a human deliberately signed this verdict off, as opposed to it merely being the matcher
    /// default the review seeds for EVERY old hold. The two are otherwise the same row — and
    /// <see cref="UpdatedAt"/> cannot tell them apart either, because seeding writes it — so this is the
    /// only fact that says "someone looked at this hold". Review metadata: the promote never reads it.
    /// <para>
    /// False for every pre-existing row and for every seeded or bulk-rewritten verdict. Once true it
    /// survives a re-seed and a bulk rewrite of the carryover half, and is cleared only when the verdict
    /// it was given about changes without a confirmation, or when a caller clears it explicitly.
    /// </para>
    /// </summary>
    public bool Confirmed { get; set; }

    /// <summary>The wall admin who confirmed the verdict; null while <see cref="Confirmed"/> is false.</summary>
    public Guid? ConfirmedByUserId { get; set; }

    /// <summary>When the verdict was confirmed; null while <see cref="Confirmed"/> is false.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
