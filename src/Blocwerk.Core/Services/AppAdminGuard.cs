using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Shared "is this user an administrator of the whole installation" check, decided against the
/// database exactly as <c>AppAdminHandler</c> decides the <c>AppAdmin</c> policy — the role is a
/// column, never a claim, so a principal can neither carry nor forge it.
/// </summary>
/// <remarks>
/// The sibling of <see cref="WallAdminGuard"/> for the authority that is NOT wall-shaped. Kept
/// separate from the policy handler because services must fail closed on their own rather than
/// trusting the page or controller in front of them to have gated the call.
/// </remarks>
internal static class AppAdminGuard
{
    public static async Task<bool> IsAppAdminAsync(
        BlocwerkDbContext db,
        Guid actingUserId,
        CancellationToken ct)
    {
        if (actingUserId == Guid.Empty)
        {
            return false;
        }

        // A tombstoned account keeps its row; it must not keep its authority.
        return await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(u => u.Id == actingUserId && u.DeletedAt == null && u.Role == IdentityRole.Admin, ct);
    }

    public static async Task EnsureAppAdminAsync(
        BlocwerkDbContext db,
        Guid actingUserId,
        CancellationToken ct)
    {
        if (!await IsAppAdminAsync(db, actingUserId, ct))
        {
            throw new UnauthorizedAccessException(
                $"User {actingUserId} is not an administrator of this installation.");
        }
    }
}
