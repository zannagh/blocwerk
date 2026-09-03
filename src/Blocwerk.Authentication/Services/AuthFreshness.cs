using System.Globalization;
using System.Security.Claims;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Stamps and reads the moment a session actually authenticated, so a resolution can tell a login
/// that just happened from a cookie minted hours ago.
/// </summary>
/// <remarks>
/// This exists for exactly one decision: whether a sign-in is allowed to CREATE an account. A
/// deleted account has had its provider identities dropped and its identifier rewritten, so a
/// still-valid cookie for it resolves to nothing at all and would otherwise fall through to the
/// "no user yet, make one" branch — silently minting a brand-new account from a stale (or stolen)
/// cookie, with no fresh consent from the provider behind it. Requiring the sign-in to be recent
/// closes that: a real signup resolves within seconds of the OAuth callback, an 8-hour-old cookie
/// never does.
/// <para>
/// A cookie issued before this claim existed simply has no stamp and counts as not fresh. That is
/// the safe direction: it can still resolve an existing account (every branch above creation), and
/// only loses the ability to conjure a new one.
/// </para>
/// </remarks>
public static class AuthFreshness
{
    /// <summary>The claim carrying the sign-in instant as Unix seconds.</summary>
    public const string ClaimType = "auth_time";

    /// <summary>
    /// How long after signing in a session still counts as "just authenticated". Generous enough to
    /// cover the redirect chain and a slow first page, far short of a session's lifetime.
    /// </summary>
    private static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(10);

    /// <summary>The claim to add to a principal at sign-in.</summary>
    public static Claim Stamp()
    {
        return new Claim(
            ClaimType,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The range <see cref="DateTimeOffset.FromUnixTimeSeconds"/> will actually accept. A value
    /// outside it throws rather than returning a date, and this method's contract is that anything
    /// malformed reads as "not fresh" — never as an exception out of the resolution path.
    /// </summary>
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>True when this identity authenticated within <see cref="FreshWindow"/>.</summary>
    public static bool IsFresh(ClaimsIdentity? identity)
    {
        if (identity?.FindFirst(ClaimType)?.Value is not { Length: > 0 } raw
            || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        // long.TryParse happily accepts a number no calendar can hold ("9999999999999"), and a claim
        // is only as trustworthy as whoever minted it. Refuse it here rather than let it throw.
        if (unixSeconds < MinUnixSeconds || unixSeconds > MaxUnixSeconds)
        {
            return false;
        }

        var signedInAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var now = DateTimeOffset.UtcNow;

        // A stamp from the future is a clock skew or a forgery; either way it is not evidence of a
        // recent sign-in, so only the backward window counts.
        return signedInAt <= now.AddMinutes(1) && now - signedInAt <= FreshWindow;
    }
}
