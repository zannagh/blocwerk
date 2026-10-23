// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Kiosk;
using Microsoft.AspNetCore.Diagnostics;

namespace Blocwerk.Web;

/// <summary>
/// Gives a registered kiosk tablet a way off any error page: a bodiless 4xx/5xx on a page navigation becomes a small
/// page that goes back to the tablet's wall on its own.
/// </summary>
/// <remarks>
/// A tablet has no keyboard and usually no address bar, so a bare error page is a dead end until somebody reloads
/// it by hand (a 400 from the idle release did exactly that in production). The status code is kept — only the
/// body is filled in — so nothing that reads the status, a script or a test, sees a different outcome.
/// <para>
/// <b>Narrow.</b> Only for a browser that carries a valid device cookie, only for document navigations (never
/// <c>/api</c>, fetches, enhanced navigation or the circuit), and only when nothing has written a body already.
/// </para>
/// <para>
/// <b>Loop guard.</b> When the failing page IS the wall page, the retry waits a minute instead of a few seconds,
/// so a wall that keeps failing is retried slowly until the server recovers rather than hammered.
/// </para>
/// </remarks>
public static class KioskErrorRecovery
{
    /// <summary>Seconds before the tablet goes back to its wall from an error elsewhere.</summary>
    public const int RetrySeconds = 5;

    /// <summary>Seconds before the tablet retries when the wall page itself failed.</summary>
    public const int WallRetrySeconds = 60;

    public static void UseKioskErrorRecovery(this WebApplication app)
    {
        app.UseStatusCodePages(context => RecoverAsync(context.HttpContext));
    }

    /// <summary>Writes the recovery page when <paramref name="http"/> is a kiosk navigation that failed.</summary>
    public static Task RecoverAsync(HttpContext http)
    {
        if (!IsDocumentNavigation(http.Request))
        {
            return Task.CompletedTask;
        }

        var registration = http.RequestServices.GetService<KioskDeviceCookie>()?.Read(http);
        if (registration is null)
        {
            return Task.CompletedTask;
        }

        var wall = $"/walls/{registration.WallId}";
        var delay = http.Request.Path.Equals(wall, StringComparison.OrdinalIgnoreCase) ? WallRetrySeconds : RetrySeconds;

        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentType = "text/html; charset=utf-8";
        return http.Response.WriteAsync(Markup(wall, delay));
    }

    private static bool IsDocumentNavigation(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dest = request.Headers["Sec-Fetch-Dest"].ToString();
        if (!string.IsNullOrEmpty(dest))
        {
            return string.Equals(dest, "document", StringComparison.OrdinalIgnoreCase);
        }

        var accept = request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("blazor-enhanced-nav", StringComparison.OrdinalIgnoreCase);
    }

    private static string Markup(string wall, int delay)
    {
        // The wall path is built from a Guid, so it needs no encoding.
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + $"<meta http-equiv=\"refresh\" content=\"{delay};url={wall}\">"
            + "<title>Blocwerk</title></head>"
            + "<body style=\"font-family:system-ui,sans-serif;text-align:center;padding:3rem 1rem\">"
            + "<p>Something went wrong. Going back to the wall&hellip;</p>"
            + $"<p><a href=\"{wall}\">Back to the wall</a></p>"
            + $"<script>setTimeout(function(){{location.replace(\"{wall}\");}},{delay * 1000});</script>"
            + "</body></html>";
    }
}
