using System.Globalization;
using System.Security.Claims;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Signs a browser session in on the strength of a personal API key: the ordinary
/// <see cref="UserCookieSignIn"/> principal, MARKED as a key session and BOUNDED by the key.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Marked: <c>amr=apikey</c> and <c>api_key_id</c>, so account-security actions
/// can refuse it (<see cref="ApiKeySessionClaims.IsApiKeySession"/>) and the cookie validator can
/// re-check the key (<see cref="ApiKeySessionValidator"/>).</description></item>
/// <item><description>Bounded: an absolute lifetime of at most <see cref="MaxLifetime"/> and never past
/// the key's own expiry, with <c>AllowRefresh=false</c> so the cookie handler's sliding window cannot
/// stretch it.</description></item>
/// </list>
/// </remarks>
public static class ApiKeySessionSignIn
{
    /// <summary>Ticket property holding when the key was last re-checked (round-trip "O" format).</summary>
    public const string LastCheckedItem = "bw.apikey.checked";

    /// <summary>The longest a key session lives, however long the key does.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(8);

    /// <summary>How often the cookie validator re-checks the key behind a session.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(5);

    public static ClaimsPrincipal BuildPrincipal(User user, ApiKey key)
    {
        var principal = UserCookieSignIn.BuildPrincipal(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(ApiKeySessionClaims.AuthenticationMethod, ApiKeySessionClaims.ApiKeyMethod));
        identity.AddClaim(new Claim(ApiKeySessionClaims.ApiKeyId, key.Id.ToString()));
        return principal;
    }

    public static AuthenticationProperties BuildProperties(ApiKey key, DateTimeOffset now)
    {
        var expires = now.Add(MaxLifetime);
        if (key.ExpiresAt is { } keyExpiry && keyExpiry < expires)
        {
            expires = keyExpiry;
        }

        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            IssuedUtc = now,
            ExpiresUtc = expires,
            AllowRefresh = false,
        };
        StampChecked(properties, now);
        return properties;
    }

    public static async Task SignInAsync(HttpContext http, User user, ApiKey key)
    {
        var now = DateTimeOffset.UtcNow;
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            BuildPrincipal(user, key),
            BuildProperties(key, now));
        UserCookieSignIn.AppendReturningVisitorCookie(http);
    }

    public static void StampChecked(AuthenticationProperties properties, DateTimeOffset now)
    {
        properties.Items[LastCheckedItem] = now.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>True when the key behind the session has not been re-checked within the interval.</summary>
    public static bool IsRecheckDue(AuthenticationProperties properties, DateTimeOffset now)
    {
        if (!properties.Items.TryGetValue(LastCheckedItem, out var raw)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
        {
            return true;
        }

        return now - last >= RecheckInterval || last > now;
    }
}
