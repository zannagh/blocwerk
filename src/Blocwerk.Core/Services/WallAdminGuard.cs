using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Shared "is this user allowed to administer that wall" check for the machine-facing services.
/// The wall owner counts as an admin even without an explicit member row.
/// </summary>
/// <remarks>
/// Every check here is ALSO the kiosk's wall gate. The owner branches below deliberately
/// <c>IgnoreQueryFilters()</c> — an owner without an explicit member row must still administer their
/// own wall — which means they bypass the <see cref="Entities.Wall"/> filter that otherwise pins a
/// kiosk session to one wall. Without the check in <see cref="EnsureNotForeignWall"/>, a member who
/// consented on wall A but owns wall B would keep full authority over B from the tablet in the gym.
/// Doing it here rather than at the ~23 call sites is the point: one place cannot be forgotten.
/// </remarks>
internal static class WallAdminGuard
{
    public static async Task<bool> IsWallAdminAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid actingUserId,
        CancellationToken ct)
    {
        if (IsForeignKioskWall(db, wallId))
        {
            return false;
        }

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
        EnsureNotForeignWall(db, wallId);

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
        if (IsForeignKioskWall(db, wallId))
        {
            return false;
        }

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
        EnsureNotForeignWall(db, wallId);

        if (!await IsWallModeratorOrAboveAsync(db, wallId, actingUserId, ct))
        {
            throw new UnauthorizedAccessException($"User {actingUserId} may not edit holds on wall {wallId}.");
        }
    }

    /// <summary>
    /// True when this context belongs to a kiosk tablet and the wall being administered is not the
    /// one the tablet is registered to.
    /// </summary>
    internal static bool IsForeignKioskWall(BlocwerkDbContext db, Guid wallId)
    {
        return db.KioskWallId is { } kioskWallId && kioskWallId != wallId;
    }

    /// <summary>
    /// Throws <see cref="KioskRestrictedException"/> when a kiosk session reaches for a wall other
    /// than its own. Distinct from <see cref="UnauthorizedAccessException"/> on purpose: the acting
    /// user genuinely holds this authority, just not from this device.
    /// </summary>
    internal static void EnsureNotForeignWall(BlocwerkDbContext db, Guid wallId)
    {
        KioskGuard.EnsureKioskWall(kioskContext: null, db, wallId);
    }
}
