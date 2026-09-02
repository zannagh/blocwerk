namespace Blocwerk.Core.Abstractions;

/// <summary>
/// The one place that answers "may this session browse a wall with nobody signed in?".
/// </summary>
/// <remarks>
/// A wall-mounted tablet spends most of its life with NOBODY picked: it was paired to a wall, the
/// last climber released their session, and it sits there showing the wall until the next person
/// taps their name. That resting state is a first-class mode, not a signed-out visitor, and the two
/// must never be confused — a signed-out visitor is sent to sign in, while a tablet sent to sign in
/// bounces between the login page and the wall until it lands somewhere useless.
/// <para>
/// It was only ever true in the DATA layer: <c>BlocwerkDbContext</c>'s wall filter fails open at
/// <see cref="System.Guid.Empty"/> and the kiosk stamp pins the read to the tablet's own wall. The
/// PAGES still demanded a signed-in user. This helper is what lets a page and a read service agree
/// on the exception, in the same words, without either of them re-deriving it.
/// </para>
/// <para>
/// Deliberately narrow, and read-only: it grants nothing but VIEWING, and only of the ONE wall the
/// device is registered to. Every write still needs a picked user. A kiosk whose wall could not be
/// determined (<see cref="System.Guid.Empty"/> — the fail-closed value the query filter uses for a
/// device re-registered mid-session) matches no wall and is refused here too.
/// </para>
/// </remarks>
public static class KioskViewing
{
    /// <summary>
    /// The single wall this session may browse anonymously, or null when it may browse none —
    /// which is every session that is not a registered kiosk, and any kiosk whose wall is unknown.
    /// </summary>
    public static Guid? ViewableWallId(IKioskContext? kioskContext)
    {
        if (kioskContext is not { IsKiosk: true })
        {
            return null;
        }

        return kioskContext.KioskWallId is { } wallId && wallId != Guid.Empty ? wallId : null;
    }

    /// <summary>
    /// True when an anonymous caller on this session may read <paramref name="wallId"/> because the
    /// tablet is registered to exactly that wall. False for every other anonymous caller, so a
    /// signed-out visitor is still sent to sign in and a kiosk still cannot reach another wall.
    /// </summary>
    public static bool AllowsAnonymousViewOf(IKioskContext? kioskContext, Guid wallId)
    {
        return wallId != Guid.Empty && ViewableWallId(kioskContext) == wallId;
    }
}
