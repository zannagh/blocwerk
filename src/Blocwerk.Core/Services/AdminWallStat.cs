namespace Blocwerk.Core.Services;

/// <summary>
/// Per-wall figures for the admin dashboard. The "load" is left as its raw components
/// (<see cref="RecentActivityCount"/>, <see cref="ActiveSessionCount"/>) rather than a single opaque
/// score, so the page can render them however it likes.
/// </summary>
/// <param name="WallId">The wall's id.</param>
/// <param name="WallName">The wall's display name.</param>
/// <param name="MemberCount">Number of members on the wall.</param>
/// <param name="BoulderCount">Number of non-archived boulders on the wall.</param>
/// <param name="RecentActivityCount">Activity-log entries recorded on the wall in the last 30 days.</param>
/// <param name="ActiveSessionCount">Climbing sessions currently live (not yet ended) on the wall.</param>
/// <param name="LastActivityAt">Timestamp of the wall's most recent activity-log entry, or null when none.</param>
public sealed record AdminWallStat(
    Guid WallId,
    string WallName,
    int MemberCount,
    int BoulderCount,
    int RecentActivityCount,
    int ActiveSessionCount,
    DateTimeOffset? LastActivityAt);
