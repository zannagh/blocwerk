using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Middleware;

/// <summary>
/// Primes <see cref="IKioskContext"/> for the request, and refuses any path
/// <see cref="KioskRestrictions.IsBlockedPath"/> does not allow when the request comes from a kiosk.
/// </summary>
/// <remarks>
/// Runs after <c>UseAuthentication</c> — it needs the resolved principal — and before
/// <c>UseAuthorization</c>, so a blocked path is refused before any endpoint gets to run.
/// <para>
/// Priming matters as much as blocking: this is the one moment in an HTTP request where the kiosk
/// cookies are guaranteed readable, so resolving here fixes the answer for everything downstream,
/// including the database contexts created later in the request.
/// </para>
/// </remarks>
public sealed class KioskRestrictionMiddleware
{
    private readonly RequestDelegate next;

    public KioskRestrictionMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext context, IKioskContext kioskContext)
    {
        await kioskContext.InitializeAsync();

        if (!kioskContext.IsKiosk || !KioskRestrictions.IsBlockedPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        // A plain GET is a person who navigated somewhere they cannot go: send them back to the wall
        // with a marker the UI can explain. Anything else is a mutation attempt and gets a flat 403 —
        // no redirect, so a script cannot mistake the 302 for success.
        if (HttpMethods.IsGet(context.Request.Method) && kioskContext.KioskWallId is { } wallId
            && wallId != Guid.Empty)
        {
            context.Response.Redirect($"/walls/{wallId}?kiosk_blocked=1");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }
}
