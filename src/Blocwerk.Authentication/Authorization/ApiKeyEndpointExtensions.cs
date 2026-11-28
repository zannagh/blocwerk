using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Authorization;

/// <summary>Endpoint-building helpers for the API-key boundary.</summary>
public static class ApiKeyEndpointExtensions
{
    /// <summary>
    /// Rejects API-key principals on this route and records that fact as endpoint metadata, so a
    /// route under an <see cref="ApiKeySurface"/> prefix can prove it is guarded without requiring
    /// authorization (which would break anonymous share-token access).
    /// </summary>
    public static RouteHandlerBuilder DenyApiKeyPrincipals(this RouteHandlerBuilder builder)
    {
        return builder
            .AddEndpointFilter<DenyApiKeyPrincipalFilter>()
            .WithMetadata(new DeniesApiKeyPrincipals());
    }

    /// <summary>
    /// Like <see cref="DenyApiKeyPrincipals"/>, but a linked device key (<c>ClaimsPrincipal.IsDeviceKey</c>, minted only by
    /// the companion app's sign-in) gets through. Every other key, a plain personal key included, still gets the 404.
    /// The route's own policy and grant then apply to the device key's owner exactly as to that owner's cookie.
    /// </summary>
    public static RouteHandlerBuilder DenyApiKeyPrincipalsExceptDevices(this RouteHandlerBuilder builder)
    {
        return builder
            .AddEndpointFilter<DenyNonDeviceApiKeyFilter>()
            .WithMetadata(new DeniesApiKeyPrincipals { ExceptDeviceKeys = true });
    }
}
