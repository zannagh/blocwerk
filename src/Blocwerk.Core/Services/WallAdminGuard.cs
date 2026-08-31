using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Shared "is this user allowed to administer that wall" check for the machine-facing services.
/// The wall owner counts as an admin even without an explicit member row.
/// </summary>
internal static class WallAdminGuard
{
    public static async Task<bool> IsWallAdminAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid actingUserId,
        CancellationToken ct)
    {
        var membership = await db.WallMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.WallId == wallId && m.UserId == actingUserId, ct);

        if (membership?.Role == WallRole.Admin)
        {
            return true;
        }

        return await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(w => w.Id == wallId && w.OwnerId == actingUserId, ct);
    }

    public static async Task EnsureWallAdminAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid actingUserId,
        CancellationToken ct)
    {
        if (!await IsWallAdminAsync(db, wallId, actingUserId, ct))
        {
            throw new UnauthorizedAccessException($"User {actingUserId} is not an admin of wall {wallId}.");
        }
    }

    /// <summary>
    /// True if the user may use the wall hold editor: the wall owner, or a member whose role is
    /// <see cref="WallRole.Admin"/> or <see cref="WallRole.Moderator"/>. Owner and admin therefore
    /// pass this check just as they pass <see cref="IsWallAdminAsync"/>.
    /// </summary>
    public static async Task<bool> IsWallModeratorOrAboveAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid actingUserId,
        CancellationToken ct)
    {
        var membership = await db.WallMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.WallId == wallId && m.UserId == actingUserId, ct);

        if (membership?.Role is WallRole.Admin or WallRole.Moderator)
        {
            return true;
        }

        return await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(w => w.Id == wallId && w.OwnerId == actingUserId, ct);
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> unless the user may use the wall hold
    /// editor (owner, admin, or moderator).
    /// </summary>
    public static async Task EnsureWallEditorAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid actingUserId,
        CancellationToken ct)
    {
        if (!await IsWallModeratorOrAboveAsync(db, wallId, actingUserId, ct))
        {
            throw new UnauthorizedAccessException($"User {actingUserId} may not edit holds on wall {wallId}.");
        }
    }
}
