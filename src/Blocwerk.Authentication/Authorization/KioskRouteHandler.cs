using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Blocks a kiosk session from routing to any page that is not on
/// <see cref="KioskRestrictions.AllowedPageTypes"/>.
/// </summary>
/// <remarks>
/// The middleware cannot do this on its own. Once a circuit is live, navigating between Blazor pages
/// is a client-side route change: no HTTP request is made, so no middleware runs. What DOES run is
/// the router's <c>AuthorizeRouteView</c>, which evaluates the default policy and passes the
/// <see cref="RouteData"/> as the authorization resource — the seam this requirement uses.
/// <para>
/// The requirement HANDLES ITSELF from the principal's kiosk claims, so it needs no registration to
/// work and cannot be defeated by a host that forgot to add a handler. <see cref="KioskRouteHandler"/>
/// is an additional, DI-registered handler that also covers the case where the session is only a
/// kiosk by virtue of the device cookie (an ordinary login on a registered tablet). Both block by
/// calling <c>Fail</c>, which is absolute, so the pair composes as "blocked if EITHER says so".
/// </para>
/// <para>
/// Both are deliberately permissive when the resource is not route data: an HTTP endpoint evaluating
/// the same default policy is already covered by the middleware, and failing here would break every
/// <c>[Authorize]</c> API endpoint in the app.
/// </para>
/// </remarks>
public sealed class KioskRouteRequirement : IAuthorizationRequirement, IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (context.Resource is RouteData routeData
            && context.User.IsKioskPrincipal()
            && KioskRestrictions.IsBlockedPageType(routeData.PageType))
        {
            context.Fail(new AuthorizationFailureReason(this, "This page is not available on a kiosk device."));
            return Task.CompletedTask;
        }

        context.Succeed(this);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The second half of the pair described on <see cref="KioskRouteRequirement"/>: blocks the same
/// pages for a session that is a kiosk by device registration rather than by claims.
/// </summary>
public sealed class KioskRouteHandler : AuthorizationHandler<KioskRouteRequirement>
{
    private readonly IKioskContext kioskContext;

    public KioskRouteHandler(IKioskContext kioskContext)
    {
        this.kioskContext = kioskContext;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        KioskRouteRequirement requirement)
    {
        if (context.Resource is RouteData routeData
            && kioskContext.IsKiosk
            && KioskRestrictions.IsBlockedPageType(routeData.PageType))
        {
            context.Fail(new AuthorizationFailureReason(this, "This page is not available on a kiosk device."));
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
