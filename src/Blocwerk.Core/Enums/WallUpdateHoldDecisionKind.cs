namespace Blocwerk.Core.Enums;

/// <summary>
/// Which side of the carryover review a <see cref="Entities.WallUpdateHoldDecision"/> row records.
/// The two subject sets are disjoint by generation — a carry subject is a live gen-N hold, a
/// new-centre subject a staged gen-N+1 hold — so one table can carry both without ambiguity.
/// </summary>
public enum WallUpdateHoldDecisionKind
{
    /// <summary>A verdict on one OLD live hold: carried, changed or removed.</summary>
    Carry = 0,

    /// <summary>A keep/discard verdict on one staged centre hold the matcher found no old twin for.</summary>
    NewCentreHold = 1,
}
