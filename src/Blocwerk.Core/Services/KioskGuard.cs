using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;

namespace Blocwerk.Core.Services;

/// <summary>
/// The two kiosk refusals the Core services share: "no kiosk session may do this at all", and
/// "not on a wall other than the tablet's own".
/// </summary>
/// <remarks>
/// Both questions have TWO possible sources and either one is enough to answer "kiosk", because
/// they fail in different directions:
/// <list type="bullet">
/// <item><description><see cref="IKioskContext"/> is the authoritative session state, but it is an
/// optional dependency: a host that never registered it (tooling, most unit tests) hands every
/// service a null and would answer "not a kiosk" for everything.</description></item>
/// <item><description><see cref="BlocwerkDbContext.KioskWallId"/> is stamped centrally by
/// <see cref="KioskScopedDbContextFactory"/> on every context it creates, so it reaches services
/// that have never heard of kiosks — but a context built by any other factory is unstamped, which
/// for a restriction means silently unlocked.</description></item>
/// </list>
/// Taking the OR of the two means a miss on either side still refuses. Over-restricting is safe
/// here; these helpers only ever deny.
/// </remarks>
internal static class KioskGuard
{
    /// <summary>
    /// The wall a kiosk session is pinned to, or null when this is not a kiosk session at all.
    /// <see cref="Guid.Empty"/> means "a kiosk whose wall could not be determined", which matches no
    /// wall and therefore refuses everything — the same fail-closed value the query filter uses.
    /// </summary>
    internal static Guid? ScopedWallId(IKioskContext? kioskContext)
    {
        return kioskContext is { IsKiosk: true } ? kioskContext.KioskWallId ?? Guid.Empty : null;
    }

    /// <inheritdoc cref="ScopedWallId(IKioskContext?)"/>
    internal static Guid? ScopedWallId(IKioskContext? kioskContext, BlocwerkDbContext db)
    {
        return ScopedWallId(kioskContext) ?? db.KioskWallId;
    }

    /// <summary>
    /// Refuses an action that is blocked for EVERY kiosk session, whatever authority the acting user
    /// otherwise holds over the wall.
    /// </summary>
    /// <remarks>
    /// <paramref name="action"/> is a sentence-leading description, e.g. "Generating a share link".
    /// </remarks>
    internal static void EnsureNotKiosk(IKioskContext? kioskContext, BlocwerkDbContext db, string action)
    {
        Refuse(ScopedWallId(kioskContext, db), action);
    }

    /// <summary>
    /// The same refusal where no <see cref="BlocwerkDbContext"/> is at hand, so only the session
    /// state can answer. Prefer the overload taking one whenever a context exists.
    /// </summary>
    internal static void EnsureNotKiosk(IKioskContext? kioskContext, string action)
    {
        Refuse(ScopedWallId(kioskContext), action);
    }

    private static void Refuse(Guid? scopedWallId, string action)
    {
        if (scopedWallId is not null)
        {
            throw new KioskRestrictedException($"{action} is not available from a kiosk device.");
        }
    }

    /// <summary>
    /// Refuses authority over any wall other than the kiosk's own. A non-kiosk session passes
    /// untouched.
    /// </summary>
    internal static void EnsureKioskWall(IKioskContext? kioskContext, BlocwerkDbContext db, Guid wallId)
    {
        if (ScopedWallId(kioskContext, db) is { } scoped && scoped != wallId)
        {
            throw new KioskRestrictedException(
                $"A kiosk device may only administer the wall it is registered to, not wall {wallId}.");
        }
    }
}
