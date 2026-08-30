using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// Login-UX cookie wiring: the "returning visitor" marker that lets the landing page skip Get Started,
/// and the per-provider usage counter that auto-enables remember-my-method after repeated sign-ins.
/// </summary>
public partial class AccountController
{
    // Set once any sign-in on this device succeeds. A later logged-OUT visit to "/" reads it and jumps
    // straight to /account/login (skipping the Get Started landing) instead of re-onboarding.
    private const string ReturningVisitorCookie = "blocwerk-returning";

    // Compact per-provider sign-in counter (e.g. "github:2|google:1"). Once a provider reaches the
    // auto-remember threshold we set RememberedMethodCookie for the user automatically.
    private const string MethodCountsCookie = "bw_method_counts";

    // Transient marker set by ExternalLogin when the user explicitly UNticked "remember my method" for
    // this login. Callback honours it (no auto-remember this time) and clears it, but keeps counting.
    private const string MethodOptOutCookie = "bw_method_optout";

    // Reaching this many sign-ins with one provider auto-enables remember-my-method for it.
    private const int AutoRememberThreshold = 3;

    /// <summary>
    /// Marks this device as a returning visitor so the Get Started landing is skipped on the next
    /// logged-out visit to "/". Set on every successful sign-in path.
    /// </summary>
    private void SetReturningVisitorCookie()
    {
        Response.Cookies.Append(ReturningVisitorCookie, "1", new CookieOptions
        {
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(365),
            Path = "/",
        });
    }

    /// <summary>
    /// Records one more successful OAuth sign-in with <paramref name="provider"/> and, once that
    /// provider crosses <see cref="AutoRememberThreshold"/>, auto-enables remember-my-method for it —
    /// unless the user explicitly opted out on THIS login (the count still advances either way).
    /// </summary>
    private void TrackProviderUsageAndMaybeRemember(string provider)
    {
        if (string.IsNullOrEmpty(provider))
        {
            return;
        }

        var counts = MethodUsageCounts.Parse(Request.Cookies[MethodCountsCookie]);
        var used = MethodUsageCounts.Increment(counts, provider);
        Response.Cookies.Append(MethodCountsCookie, MethodUsageCounts.Serialize(counts), MethodCountsCookieOptions());

        // An explicit opt-out wins for the current login, but the count above still grows so the
        // threshold accumulates over time.
        var explicitlyOptedOut = Request.Cookies.ContainsKey(MethodOptOutCookie);
        if (explicitlyOptedOut)
        {
            Response.Cookies.Delete(MethodOptOutCookie);
        }

        if (used >= AutoRememberThreshold && !explicitlyOptedOut)
        {
            Response.Cookies.Append(RememberedMethodCookie, provider, RememberedMethodCookieOptions());
        }
    }

    // Marks the explicit "don't remember my method" choice so it survives the OAuth round-trip to
    // Callback. Short-lived: Callback clears it, and it self-expires if the round-trip never completes.
    private void SetMethodOptOutMarker()
    {
        Response.Cookies.Append(MethodOptOutCookie, "1", new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/",
        });
    }

    private CookieOptions MethodCountsCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = TimeSpan.FromDays(365),
        Path = "/",
    };
}
