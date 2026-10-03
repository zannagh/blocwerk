using System.Security.Claims;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The one place that signs an EXISTING, already-verified user into the browser cookie. Shared by
/// the password/TOTP login, the API-key login and the Development-only <c>/dev/login</c>, so all of
/// them produce the same session claim for claim.
/// </summary>
/// <remarks>
/// The principal carries the exact user id as a <c>"uid"</c> claim, so <c>CurrentUserService</c>
/// resolves the session by id (its terminal path 0) — precise, never misresolving, and never
/// creating a blank user when the display name contains <c>"__"</c>. The NameIdentifier/Name claims
/// stay for the legacy identifier path and for anything that reads the display name off the
/// principal. The cookie is the whole browser session: no JWT or refresh token is minted for it.
/// </remarks>
public static class UserCookieSignIn
{
    /// <summary>
    /// Set once any sign-in on this device succeeds. A later logged-OUT visit to "/" reads it and
    /// jumps straight to /account/login (skipping the Get Started landing) instead of re-onboarding.
    /// </summary>
    public const string ReturningVisitorCookie = "blocwerk-returning";

    /// <summary>Builds the cookie principal for <paramref name="user"/>.</summary>
    public static ClaimsPrincipal BuildPrincipal(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserAuthId),
            new(ClaimTypes.Name, user.UserName),
            new("Name", user.UserName),
            new("uid", user.Id.ToString()),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// "Keep me signed in" → a persistent, long-lived (1-year absolute) cookie that overrides the
    /// cookie handler's 8h sliding default. Otherwise a session cookie that ends when the browser
    /// closes (the handler's sliding window still bounds the ticket).
    /// </summary>
    public static AuthenticationProperties BuildProperties(bool isPersistent)
    {
        return isPersistent
            ? new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddYears(1),
                AllowRefresh = true,
            }
            : new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true,
            };
    }

    /// <summary>
    /// Signs <paramref name="user"/> in with the cookie scheme and marks the device as a returning
    /// visitor, exactly as every interactive sign-in does.
    /// </summary>
    public static Task SignInAsync(HttpContext http, User user, bool isPersistent)
    {
        return SignInAsync(http, user, BuildProperties(isPersistent));
    }

    /// <summary>
    /// As <see cref="SignInAsync(HttpContext, User, bool)"/>, with caller-chosen cookie lifetime
    /// properties (the Development-only dev login keeps its own 30-day window).
    /// </summary>
    public static async Task SignInAsync(HttpContext http, User user, AuthenticationProperties properties)
    {
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(user), properties);
        AppendReturningVisitorCookie(http);
    }

    /// <summary>Marks this device as a returning visitor (a best-effort UX cookie, no identity).</summary>
    public static void AppendReturningVisitorCookie(HttpContext http)
    {
        http.Response.Cookies.Append(ReturningVisitorCookie, "1", new CookieOptions
        {
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(365),
            Path = "/",
        });
    }
}
