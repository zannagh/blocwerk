using System.Security.Claims;

namespace Blocwerk.Authentication.Kiosk;

/// <summary>
/// The claims a kiosk "acting as" session carries in the ordinary auth cookie, on top of the claims
/// a normal sign-in issues.
/// </summary>
/// <remarks>
/// Putting them in the auth cookie — rather than in circuit state — is what makes the kiosk session
/// work on the HTTP write paths that have no circuit at all (the offline replay controllers), and
/// what makes the restriction travel with the identity: any code that can resolve the acting user
/// can also see that this is a kiosk.
/// </remarks>
public static class KioskClaims
{
    /// <summary>The <c>ApiKey.Id</c> of the kiosk key the tablet registered with.</summary>
    public const string KeyId = "kiosk_key_id";

    /// <summary>The one wall the tablet is registered to.</summary>
    public const string WallId = "kiosk_wall_id";

    /// <summary>
    /// Unix seconds of the last observed activity. This is the idle clock: the cookie's own
    /// expiry is refreshed from it, and a session idle for longer than the window is rejected.
    /// </summary>
    public const string LastSeen = "kiosk_seen";

    /// <summary>How long a kiosk session may sit idle before it is dropped back to anonymous.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How stale the <see cref="LastSeen"/> claim may get before the cookie is rewritten. Without
    /// this the cookie would be re-issued on literally every request, including the SignalR traffic.
    /// It is the granularity of the idle clock, not a second timeout.
    /// </summary>
    public static readonly TimeSpan SlideInterval = TimeSpan.FromMinutes(1);

    /// <summary>True when the principal is a kiosk "acting as" session.</summary>
    public static bool IsKioskPrincipal(this ClaimsPrincipal? principal)
    {
        return principal?.FindFirst(KeyId) is not null;
    }

    /// <summary>Reads a Guid-valued kiosk claim, or null when it is absent or malformed.</summary>
    public static Guid? ReadGuid(this ClaimsPrincipal? principal, string claimType)
    {
        var value = principal?.FindFirst(claimType)?.Value;
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    /// <summary>Reads <see cref="LastSeen"/>, or null when it is absent or malformed.</summary>
    public static DateTimeOffset? ReadLastSeen(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(LastSeen)?.Value;
        if (!long.TryParse(value, out var unixSeconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    /// <summary>Formats a <see cref="LastSeen"/> value.</summary>
    public static string FormatLastSeen(DateTimeOffset moment)
    {
        return moment.ToUnixTimeSeconds().ToString();
    }
}
