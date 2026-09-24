using System.Security.Claims;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The claims that mark a browser session signed in with a personal API key, and the one helper that
/// reads them. Only <see cref="ApiKeySessionSignIn"/> writes them.
/// </summary>
public static class ApiKeySessionClaims
{
    /// <summary>Authentication method reference (RFC 8176 style).</summary>
    public const string AuthenticationMethod = "amr";

    /// <summary>The <see cref="AuthenticationMethod"/> value of a key session.</summary>
    public const string ApiKeyMethod = "apikey";

    /// <summary>The id of the key the session was signed in with.</summary>
    public const string ApiKeyId = "api_key_id";

    /// <summary>
    /// True when <paramref name="principal"/> is a cookie session signed in with an API key. The one
    /// check every account-security refusal goes through.
    /// </summary>
    public static bool IsApiKeySession(ClaimsPrincipal? principal)
    {
        return principal?.HasClaim(AuthenticationMethod, ApiKeyMethod) == true;
    }

    /// <summary>The session's key id, or null when it is not a (well-formed) key session.</summary>
    public static Guid? ReadApiKeyId(ClaimsPrincipal? principal)
    {
        return Guid.TryParse(principal?.FindFirst(ApiKeyId)?.Value, out var id) ? id : null;
    }

    /// <summary>The session user's id from the <c>uid</c> claim, or null.</summary>
    public static Guid? ReadUserId(ClaimsPrincipal? principal)
    {
        return Guid.TryParse(principal?.FindFirst("uid")?.Value, out var id) ? id : null;
    }
}
