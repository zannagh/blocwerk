// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// A same-origin check for the one form post that carries no antiforgery token (<c>/kiosk/release</c>), from the
/// headers a browser sets itself and a page cannot forge: <c>Sec-Fetch-Site</c>, and <c>Origin</c> where an older
/// browser sends no fetch metadata.
/// </summary>
public static class SameOriginPost
{
    /// <summary>
    /// False only when the browser says the post came from another site. A request with neither header is not a
    /// browser a cross-site page could drive, so it passes.
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (!string.IsNullOrEmpty(site))
        {
            return string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase);
        }

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        // The authority only: behind the proxy the scheme the app sees need not be the one the browser used.
        return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            && string.Equals(parsed.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
