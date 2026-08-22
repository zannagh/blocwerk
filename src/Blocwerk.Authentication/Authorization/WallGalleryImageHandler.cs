using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

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
        if (http is not null && !string.IsNullOrEmpty(http.Request.Query["token"]))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
