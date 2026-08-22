namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Claim types carried by a principal that authenticated with an API key. They are namespaced with
/// <c>blocwerk:</c> so they cannot collide with the OAuth/JWT claims of a signed-in user.
/// </summary>
public static class ApiKeyClaimTypes
{
    /// <summary>The key's scope, as the name of an <c>ApiKeyScope</c> value ("Wall" or "User").</summary>
    public const string Scope = "blocwerk:apikey_scope";

    /// <summary>The id of the <c>ApiKey</c> row the request authenticated with.</summary>
    public const string ApiKeyId = "blocwerk:apikey_id";

    /// <summary>The wall a wall-scoped key is bound to. Absent on user-scoped keys.</summary>
    public const string WallId = "blocwerk:wall_id";
}
