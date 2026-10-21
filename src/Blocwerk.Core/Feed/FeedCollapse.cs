// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Reduces one save's mapped rows before they are written (design §5.3, N1, E3). Inside an open <see cref="FeedScope"/>
/// every wall the save touched gets one <c>wall.resync</c> instead of its rows. Outside a scope, a wall with more than
/// <see cref="MaxRowsPerWall"/> rows in one save collapses the same way. Wall-less rows (a user's own profile) are kept as
/// they are: they are few, and a wall resync would not reach them.
/// </summary>
public static class FeedCollapse
{
    /// <summary>The most fine-grained rows one save writes for one wall before it writes a resync instead.</summary>
    public const int MaxRowsPerWall = 200;

    /// <summary>The resync reason of a save that collapsed on its own (no scope).</summary>
    public const string BulkReason = "bulk";

    public static IReadOnlyList<WallChange> Apply(IReadOnlyList<WallChange> rows, FeedScopeState? scope, Guid? actorUserId, DateTimeOffset now)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var collapse = scope is not null
            ? rows.Where(r => r.WallId is not null).Select(r => r.WallId!.Value).ToHashSet()
            : rows.Where(r => r.WallId is not null)
                .GroupBy(r => r.WallId!.Value)
                .Where(g => g.Count() > MaxRowsPerWall)
                .Select(g => g.Key)
                .ToHashSet();
        if (collapse.Count == 0)
        {
            return rows;
        }

        var reason = scope?.Label ?? BulkReason;
        var result = new List<WallChange>();
        var resynced = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (row.WallId is not { } wallId || !collapse.Contains(wallId))
            {
                result.Add(row);
            }
            else if (resynced.Add(wallId))
            {
                // In place of the wall's first row, so the order of the walls' rows is kept.
                result.Add(FeedRows.Resync(wallId, reason, actorUserId, now));
            }
        }

        return result;
    }
}
