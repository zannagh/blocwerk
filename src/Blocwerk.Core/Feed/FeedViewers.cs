// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The <see cref="FeedViewer"/> a signed-in user gets on a wall, read from the database (never from a cached aggregate).
/// The editors (owner, admin, moderator: <c>WallAdminGuard.EnsureWallEditorAsync</c>) get the Setters audience, because
/// staged update work is narrowed to Setters; plain members get Members. Kiosk tablets never come here: they subscribe
/// with <see cref="FeedViewer.Kiosk"/>.
/// </summary>
public static class FeedViewers
{
    /// <summary>The viewer for <paramref name="userId"/> on <paramref name="wallId"/>, or null when they have no access.</summary>
    public static async Task<FeedViewer?> ForUserAsync(
        BlocwerkDbContext db, Guid wallId, Guid userId, CancellationToken cancellationToken = default)
    {
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new
            {
                w.OwnerId,
                Role = w.Members.Where(m => m.UserId == userId).Select(m => (WallRole?)m.Role).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (wall is null)
        {
            return null;
        }

        return wall.OwnerId == userId ? FeedViewer.Setter(userId) : ForRole(wall.Role, userId);
    }

    /// <summary>The viewer of a member with <paramref name="role"/>, or null for no membership.</summary>
    public static FeedViewer? ForRole(WallRole? role, Guid userId) => role switch
    {
        WallRole.Admin or WallRole.Moderator => FeedViewer.Setter(userId),
        WallRole.Member => FeedViewer.Member(userId),
        _ => null,
    };

    /// <summary>
    /// The viewer named by an <c>access.revoked</c> payload's role (a demotion), or null when the role is unknown.
    /// </summary>
    public static FeedViewer? ForRoleName(string? role, Guid userId) =>
        Enum.TryParse<WallRole>(role, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? ForRole(parsed, userId)
            : null;

    /// <summary>True when both viewers see the same shared audiences as the same user.</summary>
    public static bool SameAs(this FeedViewer viewer, FeedViewer other) =>
        viewer.UserId == other.UserId && viewer.Audiences.SetEquals(other.Audiences);
}
