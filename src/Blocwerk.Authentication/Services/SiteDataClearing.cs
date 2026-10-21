// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// <c>Clear-Site-Data: "cache"</c> on every identity switch, so the browser's HTTP cache drops the
/// <c>private, max-age=31536000, immutable</c> asset responses (<c>/api/walls/{w}/a/{sha}</c>) the previous identity
/// fetched. Without it, a shared device (a kiosk tablet, a family laptop) would keep serving the last member's walls
/// from its HTTP cache for a year, to whoever uses it next, without asking the server.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>"cache"</c>, never <c>"storage"</c>: storage holds the offline queue's unsent ticks and the service
/// worker, which an identity switch must not destroy. The service worker's own asset caches are wiped by the page
/// (design §6.6, rule 1), not by this header.
/// </para>
/// <para>
/// Two layers: <see cref="Configure"/> hooks the cookie handler's sign-in and sign-out, so EVERY switch made through
/// the cookie scheme carries the header (logins of every kind, kiosk act-as, forced sign-outs by the session
/// validators); and <see cref="Apply"/> is called directly where an identity switch does not pass through the cookie
/// handler (a kiosk registration or unregistration only moves the device cookie) or must not depend on it (logout,
/// kiosk release).
/// </para>
/// </remarks>
public static class SiteDataClearing
{
    /// <summary>The response header.</summary>
    public const string HeaderName = "Clear-Site-Data";

    /// <summary>The header value: the HTTP cache only (a quoted string, as the header's grammar requires).</summary>
    public const string CacheOnly = "\"cache\"";

    /// <summary>Marks <paramref name="response"/> to clear the browser's HTTP cache for this origin.</summary>
    public static void Apply(HttpResponse response)
    {
        if (!response.HasStarted)
        {
            response.Headers[HeaderName] = CacheOnly;
        }
    }

    /// <summary>Adds the header to every cookie sign-in and sign-out, chained after any handler already set.</summary>
    public static void Configure(CookieAuthenticationOptions options)
    {
        var signingIn = options.Events.OnSigningIn;
        options.Events.OnSigningIn = async context =>
        {
            await signingIn(context);
            Apply(context.Response);
        };

        var signingOut = options.Events.OnSigningOut;
        options.Events.OnSigningOut = async context =>
        {
            await signingOut(context);
            Apply(context.Response);
        };
    }
}
