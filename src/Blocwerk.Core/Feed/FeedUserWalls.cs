// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Expands a wall-less <c>user.profile</c> row meant for a shared audience into one row per wall the user belongs to
/// (member or owner), so every wall that shows their name re-reads it. One batched lookup per save.
/// </summary>
internal static class FeedUserWalls
{
    public static bool Needs(WallChange row) =>
        row.Kind == FeedKinds.UserProfile && row.WallId is null && row.Audience != FeedAudience.User && row.EntityId is not null;

    public static async Task<IReadOnlyList<WallChange>> ApplyAsync(
        DbContext context, IReadOnlyList<WallChange> rows, bool async, CancellationToken cancellationToken)
    {
        var users = rows.Where(Needs).Select(r => r.EntityId!.Value).ToHashSet();
        if (users.Count == 0)
        {
            return rows;
        }

        // Under the lookup savepoint, so a failed lookup cannot abort the caller's open transaction.
        var walls = async
            ? await FeedLookupSavepoint.RunAsync(context, () => WallsAsync(context, users, async: true, cancellationToken), cancellationToken)
            : FeedLookupSavepoint.Run(context, () => WallsAsync(context, users, async: false, cancellationToken).GetAwaiter().GetResult());
        var result = new List<WallChange>(rows.Count);
        foreach (var row in rows)
        {
            if (!Needs(row))
            {
                result.Add(row);
                continue;
            }

            foreach (var wallId in walls.Where(w => w.UserId == row.EntityId).Select(w => w.WallId).Distinct())
            {
                result.Add(CopyFor(row, wallId));
            }
        }

        return result;
    }

    private static async Task<List<(Guid UserId, Guid WallId)>> WallsAsync(
        DbContext context, HashSet<Guid> users, bool async, CancellationToken cancellationToken)
    {
        var ids = users.ToList();
        var members = context.Set<WallMember>().AsNoTracking().IgnoreQueryFilters()
            .Where(m => ids.Contains(m.UserId)).Select(m => new { m.UserId, m.WallId });
        var owners = context.Set<Wall>().AsNoTracking().IgnoreQueryFilters()
            .Where(w => ids.Contains(w.OwnerId)).Select(w => new { UserId = w.OwnerId, WallId = w.Id });
        var memberRows = async ? await members.ToListAsync(cancellationToken) : members.ToList();
        var ownerRows = async ? await owners.ToListAsync(cancellationToken) : owners.ToList();
        return memberRows.Select(m => (m.UserId, m.WallId)).Concat(ownerRows.Select(o => (o.UserId, o.WallId))).ToList();
    }

    private static WallChange CopyFor(WallChange row, Guid wallId) => new()
    {
        WallId = wallId,
        Kind = row.Kind,
        EntityId = row.EntityId,
        Audience = row.Audience,
        ActorUserId = row.ActorUserId,
        DataJson = row.DataJson,
        SchemaVersion = row.SchemaVersion,
        CreatedAt = row.CreatedAt,
    };
}
