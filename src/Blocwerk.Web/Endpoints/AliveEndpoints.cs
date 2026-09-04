using System.Text.Json;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// <c>GET /alive</c>: the liveness beacon the browser overlay polls while the server is being
/// updated. Anonymous, allocation-cheap, and touches neither the database nor any per-request
/// service.
/// </summary>
/// <remarks>
/// Deliberately NOT under <c>/api</c>. That prefix carries the cookie handler's API behaviour and
/// <c>/api/v1</c> is an API-key surface; this route must answer identically to a signed-in browser,
/// a kiosk tablet and an anonymous visitor, so it sits next to <c>/health</c> — outside any auth
/// path, with no <c>FallbackPolicy</c> to catch it and nothing to redirect.
/// <para>
/// It must also answer while the deploy gate says busy: the gate is a health check over
/// <c>EditActivityRegistry</c> and has nothing to do with routing, so this endpoint is unaffected by
/// it — which matters, because the announcement is posted precisely when the app is about to go
/// away.
/// </para>
/// </remarks>
public static class AliveEndpoints
{
    /// <summary>
    /// The beacon's wire format, pinned here rather than inherited from the app's JSON settings.
    /// </summary>
    /// <remarks>
    /// This is a contract with code that is ALREADY RUNNING in browsers: <c>maintenance.js</c> reads
    /// <c>body.instanceId</c>, and a body without a non-empty string there is treated as "no
    /// information" — never as an error. A global <c>PropertyNamingPolicy</c> change would therefore
    /// not break this endpoint loudly; it would make every deployed client stop detecting new
    /// instances, for ever, in silence. Explicit options mean the app-wide setting can be changed by
    /// somebody who has never heard of this file without taking the reload path down with it.
    /// AliveBeaconTests asserts the exact names these produce, by executing the endpoint.
    /// </remarks>
    private static readonly JsonSerializerOptions WireFormat = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapAlive(this WebApplication app)
    {
        app.MapGet("/alive", (HttpContext http, IMaintenanceAnnouncer announcer) =>
        {
            // The one header that must never be wrong. `cache: 'no-store'` in maintenance.js binds
            // the REQUEST only; it says nothing to a CDN or a caching reverse proxy sitting in
            // front of the app, and such a proxy is free to serve a stored 200 to everyone. It
            // would then be pinning the instance id to the process that has just been replaced —
            // so every client polls forever, never sees a change, and never reloads. That is the
            // whole feature failing silently, and nothing in the app could detect it. Caddy does
            // not cache today; this makes it not matter if something in front of it ever does.
            http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            http.Response.Headers.Pragma = "no-cache";

            // Read once: Current expires on read, so asking twice could answer differently.
            var announcement = announcer.Current;

            return Results.Json(
                new AliveResponse(
                    ProcessInstance.Id,
                    ProcessInstance.StartedAt,
                    announcement is not null,
                    announcement?.Message,
                    announcement?.ExpiresAt),
                WireFormat);
        })
        .AllowAnonymous()
        .WithName("Alive");
    }
}
