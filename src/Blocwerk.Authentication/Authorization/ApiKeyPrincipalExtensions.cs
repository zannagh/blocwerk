using System.Security.Claims;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Reads the API-key specific claims back off a <see cref="ClaimsPrincipal"/>, so controllers do
/// not have to know the claim type strings.
/// </summary>
public static class ApiKeyPrincipalExtensions
{
    /// <summary>True when the principal authenticated through the API key scheme.</summary>
    public static bool IsApiKeyPrincipal(this ClaimsPrincipal principal)
    {
        return principal.Identities.Any(i =>
            i.IsAuthenticated
            && string.Equals(i.AuthenticationType, ApiKeyAuthenticationHandler.SchemeName, StringComparison.Ordinal));
    }

    /// <summary>The scope of the API key the request authenticated with, or null when there is none.</summary>
    public static ApiKeyScope? GetApiKeyScope(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ApiKeyClaimTypes.Scope);
        if (value is null || !Enum.TryParse<ApiKeyScope>(value, ignoreCase: false, out var scope))
        {
            return null;
        }

        return scope;
    }

    /// <summary>The id of the API key the request authenticated with, or null when there is none.</summary>
    public static Guid? GetApiKeyId(this ClaimsPrincipal principal)
    {
        return ParseGuid(principal.FindFirstValue(ApiKeyClaimTypes.ApiKeyId));
    }

    /// <summary>The wall a wall-scoped key is bound to, or null for any other principal.</summary>
    public static Guid? GetApiKeyWallId(this ClaimsPrincipal principal)
    {
        return ParseGuid(principal.FindFirstValue(ApiKeyClaimTypes.WallId));
    }

    private static Guid? ParseGuid(string? value)
    {
        if (value is null || !Guid.TryParse(value, out var parsed))
        {
            return null;
        }

        return parsed;
    }
}
