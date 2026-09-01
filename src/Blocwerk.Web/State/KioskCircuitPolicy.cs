using Blocwerk.Authentication.Kiosk;

namespace Blocwerk.Web.State;

/// <summary>
/// The decision half of the in-circuit kiosk gate: when a live circuit's kiosk session must end, and
/// how often its credentials are worth re-reading from the database.
/// </summary>
/// <remarks>
/// Split out from <see cref="KioskCircuitHandler"/> so it can be exercised without a circuit, a
/// database or an HTTP context. Everything here is a pure function of a clock.
/// </remarks>
public static class KioskCircuitPolicy
{
    /// <summary>
    /// How often the kiosk key and the member's consent are re-read while a circuit is live.
    /// </summary>
    /// <remarks>
    /// Short enough that "revoke the key" or "withdraw consent" empties the tablet while the admin
    /// is still standing at it, long enough that a burst of clicks is not a burst of queries. It is
    /// the granularity of revocation, not a timeout.
    /// </remarks>
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromSeconds(30);

    /// <summary>True when the credentials are stale enough to be worth re-reading.</summary>
    public static bool ShouldRevalidate(DateTimeOffset lastChecked, DateTimeOffset now)
    {
        return now - lastChecked >= RevalidationInterval;
    }

    /// <summary>
    /// Whether a live kiosk circuit must be dropped back to anonymous.
    /// </summary>
    /// <param name="isKioskSession">
    /// False for every ordinary session, and for a kiosk tablet that is only browsing anonymously.
    /// Both leave this method with <c>false</c>, which is what keeps normal circuits untouched.
    /// </param>
    /// <param name="lastActivity">
    /// The last inbound activity on the circuit, seeded from the session's <c>kiosk_seen</c> claim so
    /// the clock starts where the cookie left it rather than at circuit open.
    /// </param>
    /// <param name="now">The current time.</param>
    /// <param name="credentialsRevoked">
    /// Set once a revalidation has found the kiosk key gone or the consent withdrawn. Sticky: a
    /// session that has been refused must not come back if a later query happens to succeed.
    /// </param>
    public static bool ShouldEndSession(
        bool isKioskSession,
        DateTimeOffset lastActivity,
        DateTimeOffset now,
        bool credentialsRevoked)
    {
        if (!isKioskSession)
        {
            return false;
        }

        // The same 30 minutes the cookie validator enforces on HTTP requests, deliberately read from
        // the same constant so the two gates can never drift apart.
        return credentialsRevoked || KioskSessionValidator.IsIdleExpired(lastActivity, now);
    }
}
