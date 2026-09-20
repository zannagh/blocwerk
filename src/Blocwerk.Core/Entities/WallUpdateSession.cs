using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One whole-wall big update as a resumable unit of work: the staged panels (implicitly, via
/// <see cref="StagedGeneration"/>), every decision the user has made about them, and where in the
/// wizard they had got to. Before this existed, "an update is in flight" was implied by staged
/// <see cref="WallPanel"/> rows alone and every decision lived in the browser's memory, so a lost
/// circuit meant redoing the whole carryover review.
/// <para>
/// Modelled on <see cref="ChangeJournalBatch"/>: an open/sealed lifecycle with child rows under it
/// (<see cref="WallUpdateHoldDecision"/>, <see cref="WallUpdateNeighbourDecision"/>), which cascade
/// away with the session.
/// </para>
/// <para>
/// Per WALL, not per user: at most one row per wall is <see cref="WallUpdateSessionStatus.Open"/>, and
/// any wall admin may read and continue it — a second moderator helping finish an update is the point.
/// <see cref="CreatedByUserId"/> records who started it, <see cref="LastActiveByUserId"/> who touched it last.
/// </para>
/// <para>
/// NOT journalled. A change-journal revert of a wall-update batch therefore deletes the staged panels and
/// holds (and cascades the decision rows away) but leaves this header Open with nothing under it; the next
/// staging then refuses until someone takes it over. See the KNOWN GAP note on <c>ChangeJournalReverter</c>.
/// </para>
/// </summary>
public class WallUpdateSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>
    /// The generation the staged panels and holds carry — <c>Wall.CurrentGeneration + 1</c> at the time
    /// of staging. Stored rather than derived so a promoted/discarded session still states which update
    /// it was, after the wall generation has moved on.
    /// </summary>
    public int StagedGeneration { get; set; }

    public WallUpdateSessionStatus Status { get; set; } = WallUpdateSessionStatus.Open;

    /// <summary>The resume cursor: the step the user last reached. See <see cref="WallUpdatePhase"/>.</summary>
    public WallUpdatePhase Phase { get; set; } = WallUpdatePhase.Detected;

    /// <summary>
    /// How far into the per-neighbour overlap walk the user had got, as an index into the session's
    /// neighbour panels ordered by (Row, Col) — the same order the review builds them in.
    /// </summary>
    public int NeighbourIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    /// <summary>When any decision or the phase cursor was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The wall admin who most recently wrote to the session — not necessarily its creator.</summary>
    public Guid? LastActiveByUserId { get; set; }

    /// <summary>When the session stopped being open (promoted or discarded), or null while it is OPEN.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}
