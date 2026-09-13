namespace Blocwerk.Core.Enums;

/// <summary>
/// Outcome of the optional OpenCV carryover auto-matching pass in a big-wall update. The session is
/// always returned fully populated (carry-all works with zero proposals); this only tells the UI
/// whether it may show a "map changed holds manually" banner instead of silently seeding nothing.
/// </summary>
public enum AutoMatchStatus
{
    /// <summary>The matcher ran and its suggestions (if any) are usable.</summary>
    Ok = 0,

    /// <summary>The matcher could not run at all — the native/OpenCV library failed to load.</summary>
    Unavailable = 1,

    /// <summary>The matcher ran but failed to produce a match (e.g. too few texture matches / homography failed).</summary>
    Failed = 2,
}
