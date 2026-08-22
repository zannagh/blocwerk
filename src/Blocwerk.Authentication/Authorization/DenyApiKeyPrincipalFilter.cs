using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Turns any request carrying an API-key principal into a 404 before the handler runs. See
/// <see cref="DeniesApiKeyPrincipals"/> for why these routes cannot simply require authorization.
/// </summary>
public sealed class DenyApiKeyPrincipalFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.User.IsApiKeyPrincipal())
        {
            // Not 403: a machine caller has no business knowing this route exists.
            return Results.NotFound();
        }

        return await next(context);
    }
}
