using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Decides <see cref="WallGalleryImageRequirement"/>. An API-key principal is rejected outright:
/// machine callers read gallery bytes through the /api route, which checks the wall against the
/// key's own wall claim, whereas this route only checks what the key's OWNER may see — so a
/// leaked wall key would otherwise read every wall its owner belongs to.
/// </summary>
public sealed class WallGalleryImageHandler : AuthorizationHandler<WallGalleryImageRequirement>
{
    private readonly IHttpContextAccessor httpContextAccessor;

    public WallGalleryImageHandler(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WallGalleryImageRequirement requirement)
    {
        if (context.User.IsApiKeyPrincipal())
        {
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Anonymous share-token viewing is a feature. The token itself is validated by the
        // endpoint against the wall it names; all this decides is that one was offered at all.
        // Endpoint routing passes the Endpoint as the resource, so the request comes from the
        // accessor — the resource is only honoured when a caller hands one in directly.
        var http = context.Resource as HttpContext ?? httpContextAccessor.HttpContext;
        if (http is null)
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrEmpty(http.Request.Query["token"]))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // The other anonymous viewer with a legitimate claim to wall bytes: a registered kiosk
        // tablet with nobody picked yet, which spends most of the day in exactly that state and
        // whose whole job is showing this wall. Like the token above, this decides only that the
        // caller may be considered at all — the ENDPOINT still checks that the bytes belong to the
        // wall the device is registered to. Resolved from the request's own scope because this
        // handler is a singleton and IKioskContext is scoped.
        // RequestServices is null on a hand-built HttpContext (and on a request that never went
        // through the routing pipeline), and "no kiosk context" simply means "not a kiosk".
        var kioskContext = http.RequestServices?.GetService<IKioskContext>();
        if (KioskViewing.ViewableWallId(kioskContext) is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
