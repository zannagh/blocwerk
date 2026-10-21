// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// <c>Clear-Site-Data: "cache"</c> where an identity leaves a device for good (design §6.6, rule 2): an account sign-out
/// and a kiosk unregistration. The browser's HTTP cache then drops the <c>private, max-age=31536000, immutable</c> asset
/// responses (<c>/api/walls/{w}/a/{sha}</c>) of the identity that left. Only <c>"cache"</c>, never <c>"storage"</c>:
/// storage holds the offline queue's unsent ticks and the service worker, and Chromium and WebKit leave Cache Storage
/// alone under <c>"cache"</c>, so the service worker's asset caches are wiped by the page (§6.6, rule 1), not by this.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always on a 200, never on a redirect.</b> WebKit reads the header only from a final response, never from a 3xx,
/// so <see cref="SignedOutPage"/> answers with a small page that carries it and then navigates on. The page fires
/// <c>bw:identity-changed</c> and calls <c>bwDeployDrafts.clearAll()</c> (it loads deploy-drafts.js for that); the next
/// server-rendered page's partition check (rule 1) wipes the service worker's caches.
/// </para>
/// <para>
/// <b>Not on kiosk identity changes.</b> Act-as, release, a registration and the idle expiry send no header. The idle
/// expiry never could: the ticket lapses, or the validator rejects it on some later request, and neither is a sign-out
/// response the tablet renders. Every identity on a tablet shares one cache partition pinned to the bound
/// wall, and a kiosk session gets every asset outside the manifest's Kiosk class as <c>no-store</c>, so nothing
/// member-only is cached on a tablet; clearing on every act-as would only throw away the wall's photos. A server-forced
/// sign-out (the session validators) is caught by the next page's partition check (rule 3) instead.
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

    /// <summary>
    /// The 200 page that ends a device's use by an identity: it carries <c>Clear-Site-Data</c>, fires
    /// <c>bw:identity-changed</c> (and <c>bwDeployDrafts.clearAll()</c> where the page has it), and goes on to
    /// <paramref name="next"/> by script, by meta refresh, and by a link for anything that runs neither.
    /// </summary>
    /// <param name="response">The response to mark.</param>
    /// <param name="next">A local path to continue to.</param>
    public static ContentResult SignedOutPage(HttpResponse response, string next)
    {
        Apply(response);
        response.Headers.CacheControl = "no-store";
        return new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "text/html; charset=utf-8",
            Content = SignedOutMarkup.Render(next),
        };
    }
}
