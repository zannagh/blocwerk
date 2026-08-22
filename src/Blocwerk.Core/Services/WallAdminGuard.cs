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
}
