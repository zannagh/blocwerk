namespace Blocwerk.Core.Enums;

/// <summary>
/// What a big-wall update does with an old (curated) hold when the wall photo is replaced.
/// </summary>
public enum CarryKind
{
    /// <summary>The old hold is kept in place, re-seated onto the matched new-centre position.</summary>
    Carried = 0,

    /// <summary>The old hold is kept but the user asserts it has physically changed; the carried hold is flagged for review. Enum value stays 1 for backward compatibility.</summary>
    Changed = 1,

    /// <summary>The old hold is gone from the wall: its boulders become historic and the hold is deleted.</summary>
    Removed = 2,
}
