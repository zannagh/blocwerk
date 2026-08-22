namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Names of the authorization policies registered by
/// <see cref="AuthenticationServices.ConfigureAuthenticationAndAuthorization"/>. Controllers
/// reference these constants instead of repeating the strings.
/// </summary>
public static class BlocwerkPolicies
{
    /// <summary>Requires an API-key principal whose key is scoped to a single wall.</summary>
    public const string WallApiKey = "WallApiKey";

    /// <summary>Requires an API-key principal whose key stands in for its owning user.</summary>
    public const string UserApiKey = "UserApiKey";

    /// <summary>Requires an API-key principal of either scope.</summary>
    public const string AnyApiKey = "AnyApiKey";

    /// <summary>
    /// Guards the browser's gallery byte route: a signed-in human or an anonymous share-token
    /// viewer, never an API key.
    /// </summary>
    public const string WallGalleryImage = "WallGalleryImage";
}
