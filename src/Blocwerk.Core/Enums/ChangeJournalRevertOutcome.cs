namespace Blocwerk.Core.Enums;

/// <summary>
/// Why a revert attempt ended the way it did. Distinguishes the three non-success paths that an
/// operator-facing UI has to phrase differently, so none of them has to be inferred from a string.
/// </summary>
public enum ChangeJournalRevertOutcome
{
    /// <summary>The inverse was applied and committed.</summary>
    Reverted = 0,

    /// <summary>No batch with that id exists.</summary>
    BatchNotFound = 1,

    /// <summary>
    /// The batch was not <see cref="ChangeJournalStatus.Recorded"/> when the claim ran: it had already
    /// been reverted, or another operator's revert of the same batch won the race. Nothing was changed.
    /// </summary>
    AlreadyReverted = 2,

    /// <summary>A precondition guard found conflicts and the revert was rolled back; nothing changed.</summary>
    Blocked = 3,
}
