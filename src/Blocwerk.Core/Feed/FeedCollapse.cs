// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Reduces one save's mapped rows before they are written (design §5.3, N1, E3). Inside an open <see cref="FeedScope"/>
/// every wall the save touched gets one <c>wall.resync</c> instead of its rows. Outside a scope, a wall with more than
/// <see cref="MaxRowsPerWall"/> rows in one save collapses the same way. Rows addressed to one user
/// (<see cref="FeedAudience.User"/>: <c>access.revoked</c>, a user's own profile) are always kept as they are and never
/// counted: they are few, and a wall resync would not reach them (nor tear down a revoked subscription).
/// </summary>
public static class FeedCollapse
{
    /// <summary>The most fine-grained rows one save writes for one wall before it writes a resync instead.</summary>
    public const int MaxRowsPerWall = 200;

    /// <summary>The resync reason of a save that collapsed on its own (no scope).</summary>
    public const string BulkReason = "bulk";

    public static IReadOnlyList<WallChange> Apply(IReadOnlyList<WallChange> rows, FeedScopeState? scope, DateTimeOffset now)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var shared = rows.Where(Collapsible).ToList();
        var collapse = scope is not null
            ? shared.Select(r => r.WallId!.Value).ToHashSet()
            : shared
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
            if (!Collapsible(row) || !collapse.Contains(row.WallId!.Value))
            {
                result.Add(row);
            }
            else if (resynced.Add(row.WallId!.Value))
            {
                // In place of the wall's first row, so the order of the walls' rows is kept. Actorless: the resync reaches
                // every viewer of the wall, while the rows it stands for may have named no one (stats) or reached only
                // setters, so it must not show anyone a byline they would not have seen.
                result.Add(FeedRows.Resync(row.WallId!.Value, reason, actorUserId: null, now));
            }
        }

        return result;
    }

    private static bool Collapsible(WallChange row) => row.WallId is not null && row.Audience != FeedAudience.User;
}
