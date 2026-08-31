namespace Blocwerk.Core.Services;

/// <summary>
/// The app-wide administration overview: global totals plus a per-wall breakdown. Computed against
/// a global (unfiltered) view of the database, so it spans every wall regardless of the viewer's
/// own memberships.
/// </summary>
/// <param name="TotalWalls">Total number of walls.</param>
/// <param name="TotalUsers">Total number of users.</param>
/// <param name="TotalBoulders">Total number of non-archived boulders.</param>
/// <param name="Walls">Per-wall statistics, ordered by recent activity then name.</param>
public sealed record AdminOverview(
    int TotalWalls,
    int TotalUsers,
    int TotalBoulders,
    IReadOnlyList<AdminWallStat> Walls);
