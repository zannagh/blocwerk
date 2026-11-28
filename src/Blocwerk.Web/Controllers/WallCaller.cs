// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The caller's standing on one wall, decided as the web wall page decides it: a member through the wall's membership
/// filter, an admin when their role is Admin or they own the wall, an editor of holds when admin or moderator.
/// </summary>
/// <param name="UserId">The caller.</param>
/// <param name="OwnerId">The wall's owner.</param>
/// <param name="Role">The caller's membership role, or null without a membership row.</param>
public sealed record WallCaller(Guid UserId, Guid OwnerId, WallRole? Role)
{
    /// <summary>True for the owner and every member.</summary>
    public bool IsMember => Role is not null || IsOwner;

    /// <summary>True when the caller owns the wall.</summary>
    public bool IsOwner => UserId == OwnerId;

    /// <summary>True for an Admin member or the owner (the web's <c>_isAdmin</c>).</summary>
    public bool IsAdmin => Role == WallRole.Admin || IsOwner;

    /// <summary>True for an admin or a moderator (the web's <c>_canEditHolds</c>).</summary>
    public bool CanEditHolds => IsAdmin || Role == WallRole.Moderator;

    /// <summary>The role as the app shows it: Admin (the owner included), Moderator or Member.</summary>
    public string RoleName => IsAdmin ? nameof(WallRole.Admin) : (Role ?? WallRole.Member).ToString();

    /// <summary>The standing of <paramref name="userId"/> on a wall whose members are loaded.</summary>
    public static WallCaller Of(Wall wall, Guid userId) =>
        new(userId, wall.OwnerId, wall.Members.FirstOrDefault(m => m.UserId == userId)?.Role);

    /// <summary>The caller's standing on <paramref name="wallId"/>, or null when they cannot see the wall at all.</summary>
    /// <exception cref="UnauthorizedAccessException">The request names nobody.</exception>
    public static async Task<WallCaller?> ReadAsync(
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        Guid wallId,
        CancellationToken ct)
    {
        var user = await currentUser.GetCurrentUserAsync();
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        var wall = await db.Walls.AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { w.OwnerId, Role = w.Members.Where(m => m.UserId == user.Id).Select(m => (WallRole?)m.Role).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        return wall is null ? null : new WallCaller(user.Id, wall.OwnerId, wall.Role);
    }
}
