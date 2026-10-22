// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Why a request carries no signed-in session (<see cref="SignOutReasons"/>), for the device's 401 lock (Phase 3 plan §1):
/// <c>expired</c> keeps a <c>u-</c> partition locked for the same user, anything else wipes it.
/// </summary>
/// <remarks>
/// <para>
/// The request that ends a session names the reason: a validator rejection is <c>forced</c>
/// (<see cref="RejectAsync"/>), a session whose claims resolve to nobody who may sign in is <c>gone</c>
/// (<c>CurrentUserService</c>), the signed-out page of a logout or kiosk unregister is <c>signed-out</c>. Any other request without a session is <c>expired</c>: an expired ticket, or no cookie
/// at all, which is what a browser sends once it dropped an expired one.
/// </para>
/// <para>
/// STICKY. That request may be one the page never reads (a worker's <c>/a/</c> fetch, a circuit start), and the cookie it
/// drops is gone from every later request, which would then read as <c>expired</c> and lock instead of wipe. So a
/// <c>forced</c>, <c>gone</c> or <c>signed-out</c> also sets the <see cref="MarkerCookie"/> (HttpOnly, <see cref="MarkerLifetime"/>, the
/// lock's own window), and a later request without a session reports the marker's reason. Signing in deletes it.
/// </para>
/// </remarks>
public static class SignOutReason
{
    /// <summary>The sticky marker of a <c>forced</c> or <c>gone</c> sign-out.</summary>
    public const string MarkerCookie = "bw-signout";

    /// <summary>As long as a device keeps a locked partition: past it, the device wiped the lock anyway.</summary>
    public static readonly TimeSpan MarkerLifetime = TimeSpan.FromDays(7);

    private static readonly object ItemKey = new();

    /// <summary>Wires the marker's removal into the cookie handler's sign-in, after any existing handler.</summary>
    public static void Configure(CookieAuthenticationOptions options)
    {
        var inner = options.Events.OnSignedIn;
        options.Events.OnSignedIn = async context =>
        {
            await inner(context);
            Forget(context.HttpContext);
        };
    }

    /// <summary>Records <paramref name="reason"/> for this request and, when it is sticky, for the device's next ones.</summary>
    public static void Mark(HttpContext? http, string reason)
    {
        if (http is null)
        {
            return;
        }

        http.Items[ItemKey] = reason;
        if (SignOutReasons.IsSticky(reason) && !http.Response.HasStarted)
        {
            http.Response.Cookies.Append(MarkerCookie, reason, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = MarkerLifetime,
            });
        }
    }

    /// <summary>The reason of a request without a session: this request's own, else the sticky marker's, else <c>expired</c>.</summary>
    public static string Of(HttpContext? http)
    {
        if (http?.Items[ItemKey] is string reason)
        {
            return reason;
        }

        var marker = http?.Request.Cookies[MarkerCookie];
        return SignOutReasons.IsSticky(marker) ? marker! : SignOutReasons.Expired;
    }

    /// <summary>Stamps a 401 under <c>/api</c> with the reason (<see cref="SignOutReasons.HeaderName"/>).</summary>
    public static void Stamp(HttpContext http) => http.Response.Headers[SignOutReasons.HeaderName] = Of(http);

    /// <summary>A validator's rejection: the principal goes, the cookie is dropped, and the reason is <c>forced</c>.</summary>
    public static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Mark(context.HttpContext, SignOutReasons.Forced);
    }

    // The marker goes: one the request carried, and one this very response set (a rejected session cookie, then a
    // sign-in in the same request), whose pending Set-Cookie is withdrawn.
    private static void Forget(HttpContext http)
    {
        var setHere = http.Items[ItemKey] is string reason && SignOutReasons.IsSticky(reason);
        http.Items.Remove(ItemKey);
        if (http.Response.HasStarted)
        {
            return;
        }

        var prefix = MarkerCookie + "=";
        var cookies = http.Response.Headers.SetCookie;
        if (cookies.Any(c => c?.StartsWith(prefix, StringComparison.Ordinal) == true))
        {
            http.Response.Headers.SetCookie = cookies.Where(c => c?.StartsWith(prefix, StringComparison.Ordinal) != true).ToArray();
        }

        if (setHere || http.Request.Cookies.ContainsKey(MarkerCookie))
        {
            http.Response.Cookies.Delete(MarkerCookie, new CookieOptions { Path = "/", Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax });
        }
    }
}
