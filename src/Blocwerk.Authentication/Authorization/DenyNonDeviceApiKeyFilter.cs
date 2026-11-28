// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Turns a request carrying any API-key principal except a linked device key into a 404 before the handler runs, the way
/// <see cref="DenyApiKeyPrincipalFilter"/> does for every key. Installed by
/// <c>ApiKeyEndpointExtensions.DenyApiKeyPrincipalsExceptDevices</c>.
/// </summary>
public sealed class DenyNonDeviceApiKeyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.User.IsNonDeviceApiKey())
        {
            // Not 403: a machine caller has no business knowing this route exists.
            return Results.NotFound();
        }

        return await next(context);
    }
}
