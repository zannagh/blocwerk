using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The one place that answers "may this session create a boulder with NOBODY signed in?".
/// </summary>
/// <remarks>
/// <b>This is the app's only unauthenticated write, so read this before changing it.</b>
/// <see cref="KioskViewing"/> is the read-only sibling and says in its own remarks that it grants
/// viewing and nothing else — that stays true. This is a separate, narrower allowance with a
/// separate name, so widening one can never silently widen the other.
/// <para>
/// FOUR conditions, all required, and each of them closes a different hole:
/// </para>
/// <list type="number">
/// <item><description>The session is a registered kiosk whose wall is exactly the wall being
/// written to — via <see cref="KioskViewing.AllowsAnonymousViewOf"/>, so the wall comes from the
/// protected device cookie / kiosk claims and NEVER from the form or the route. A device
/// re-registered mid-session resolves to <see cref="Guid.Empty"/> and is refused here too.</description></item>
/// <item><description>That registration names a kiosk API key.</description></item>
/// <item><description>The key still validates against the database — unrevoked, unexpired, still
/// kiosk-scoped, still this wall. <see cref="IKioskContext"/> alone is only safe to RESTRICT on; a
/// grant has to re-check, exactly as <c>KioskController</c> does before every act-as.</description></item>
/// <item><description>The wall opted in (<c>Wall.AllowAnonymousKioskSetting</c>), which defaults to
/// false. A gym that never turns this on has no unauthenticated write surface at all.</description></item>
/// </list>
/// <para>
/// A missing <paramref name="keyValidator"/> (hosts with no auth stack — tests, tooling) fails
/// CLOSED: no validator means the key cannot be re-checked, which means no grant.
/// </para>
/// </remarks>
public static class KioskAnonymousSetting
{
    /// <summary>
    /// True when an anonymous caller on this session may create a boulder on
    /// <paramref name="wallId"/>. False for every other anonymous caller, including a kiosk aimed at
    /// a different wall, one whose key has been revoked, and one whose wall has not opted in.
    /// </summary>
    public static async Task<bool> IsAllowedAsync(
        BlocwerkDbContext db,
        IKioskContext? kioskContext,
        IKioskKeyValidator? keyValidator,
        Guid wallId,
        CancellationToken ct = default)
    {
        if (!KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId))
        {
            return false;
        }

        if (kioskContext?.KioskApiKeyId is not { } apiKeyId || apiKeyId == Guid.Empty)
        {
            return false;
        }

        if (keyValidator is null)
        {
            return false;
        }

        if (!await keyValidator.IsKeyValidAsync(apiKeyId, wallId, ct))
        {
            return false;
        }

        // Filter-ignoring on purpose: the opt-in is read for an anonymous caller, who by definition
        // passes no membership check, and the wall identity was already pinned by the device cookie
        // above. Reading a single boolean of the one named wall widens nothing.
        return await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => w.AllowAnonymousKioskSetting)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The refusal an unacceptable setter id produces, so tests and callers agree on it.</summary>
    public const string UnconsentingSetterMessage =
        "A kiosk can only credit setters who consented to being picked at this wall's kiosk";

    /// <summary>
    /// Validates the setter ids an ANONYMOUS kiosk submitted, and returns them. Every id must belong
    /// to a member of <paramref name="wallId"/> who has CONSENTED to being picked at that wall's
    /// kiosk; anything else throws.
    /// </summary>
    /// <remarks>
    /// The ordinary create path already restricts setters to wall members, and silently drops the
    /// rest. That is not enough here, in either half. The caller is anonymous, so an id in the
    /// request would otherwise let anybody credit a route to any member of the gym they can name —
    /// hence the consent allow-list (<c>WallMember.KioskConsentedAt</c>), which is the very set the
    /// tablet's own picker is built from: the people who agreed a tablet may act in their name. And
    /// it THROWS rather than dropping, so a request naming a stranger fails loudly instead of
    /// quietly publishing a boulder credited to nobody the setter meant.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">Some id is not a consenting member.</exception>
    public static async Task<List<Guid>> ValidateSettersAsync(
        BlocwerkDbContext db,
        Guid wallId,
        IReadOnlyList<Guid>? setterUserIds,
        CancellationToken ct = default)
    {
        if (setterUserIds is not { Count: > 0 })
        {
            return [];
        }

        var requested = setterUserIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (requested.Count == 0)
        {
            return [];
        }

        var consenting = await db.WallMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.WallId == wallId && m.KioskConsentedAt != null && requested.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(ct);

        if (consenting.Count != requested.Count)
        {
            throw new UnauthorizedAccessException(UnconsentingSetterMessage);
        }

        return consenting;
    }
}
