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
}
