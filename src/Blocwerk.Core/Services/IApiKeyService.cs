using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Issues, lists, revokes and validates API keys. The full token is handed back only by the
/// create calls — afterwards only its hash exists, so a lost token can only be replaced.
/// </summary>
public interface IApiKeyService
{
    /// <summary>
    /// Issues a wall-scoped key. The acting user must be an admin member (or the owner) of the wall.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user does not administer the wall.</exception>
    Task<(ApiKey Key, string Token)> CreateWallKeyAsync(
        Guid wallId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a kiosk key for a wall-mounted tablet. The acting user must be an admin member (or the
    /// owner) of the wall. The key is deliberately <see cref="Enums.ApiKeyScope.Kiosk"/> rather than
    /// <see cref="Enums.ApiKeyScope.Wall"/>, so it does not inherit the wall write endpoints.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user does not administer the wall.</exception>
    Task<(ApiKey Key, string Token)> CreateKioskKeyAsync(
        Guid wallId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a key scoped to the user's own access. A personal key has no admin path, so the
    /// acting user may only mint their own.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user is not the named user.</exception>
    Task<(ApiKey Key, string Token)> CreateUserKeyAsync(
        Guid userId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a key that acts for the whole installation rather than for a wall or a person — the
    /// autodeploy hook is the only intended holder. Restricted to app administrators.
    /// </summary>
    /// <remarks>
    /// The key carries no wall and grants nothing beyond the endpoints written for
    /// <see cref="Enums.ApiKeyScope.Installation"/>. <paramref name="actingUserId"/> is stored as
    /// its owner purely so a key can always be traced back to the person who minted it; it does
    /// NOT stand in for that person's own access the way a
    /// <see cref="Enums.ApiKeyScope.User"/> key does.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The acting user is not an app administrator.</exception>
    Task<(ApiKey Key, string Token)> CreateInstallationKeyAsync(
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Every <see cref="Enums.ApiKeyScope.Installation"/> key, newest first, revoked and expired
    /// ones included. Restricted to app administrators, exactly as minting one is.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user is not an app administrator.</exception>
    Task<IReadOnlyList<ApiKey>> GetInstallationKeysAsync(Guid actingUserId, CancellationToken ct = default);

    /// <summary>
    /// All keys of a wall, newest first — both <see cref="Enums.ApiKeyScope.Wall"/> and
    /// <see cref="Enums.ApiKeyScope.Kiosk"/> keys, since both carry the wall's id. Revoked and expired
    /// keys are included.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user does not administer the wall.</exception>
    Task<IReadOnlyList<ApiKey>> GetWallKeysAsync(Guid wallId, Guid actingUserId, CancellationToken ct = default);

    /// <summary>
    /// All user-scoped keys of a user, newest first. Revoked and expired keys are included.
    /// A personal key has no admin path, so the acting user may only list their own.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user is not the named user.</exception>
    Task<IReadOnlyList<ApiKey>> GetUserKeysAsync(Guid userId, Guid actingUserId, CancellationToken ct = default);

    /// <summary>
    /// Revokes a key. The acting user must own it (user scope), administer its wall (wall and
    /// kiosk scope) or administer the installation (installation scope).
    /// Revoking an already revoked key is a no-op.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user may not revoke the key.</exception>
    Task RevokeAsync(Guid apiKeyId, Guid actingUserId, CancellationToken ct = default);

    /// <summary>
    /// Turns the per-key anonymous-setting flag of a <see cref="Enums.ApiKeyScope.Kiosk"/> key on or
    /// off. The acting user must administer the key's wall, exactly as revoking it requires.
    /// </summary>
    /// <remarks>
    /// This only ever NARROWS <c>Wall.AllowAnonymousKioskSetting</c>: with the wall's own switch off
    /// the tablet cannot set anonymously whatever this says. It takes effect on the next attempt of
    /// an already-registered tablet — nothing has to be re-paired.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The acting user does not administer the wall.</exception>
    /// <exception cref="InvalidOperationException">No such key, or it is not kiosk-scoped.</exception>
    Task SetAnonymousKioskSettingAsync(
        Guid apiKeyId,
        Guid actingUserId,
        bool allowed,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a bearer token to its key, or null when it is unknown, revoked or expired.
    /// Stamps <see cref="ApiKey.LastUsedAt"/>, throttled to at most one write per minute.
    /// </summary>
    /// <remarks>
    /// The returned entity carries <see cref="ApiKey.Scope"/> and <see cref="ApiKey.WallId"/>, so a
    /// caller that needs to know "which kind of key is this, and for which wall" can read them straight
    /// off the result. <see cref="ValidateKioskAsync"/> is the narrow convenience over exactly that.
    /// </remarks>
    Task<ApiKey?> ValidateAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Resolves a bearer token to the wall its kiosk key is registered to, or null when the token is
    /// invalid, revoked, expired, or not a <see cref="Enums.ApiKeyScope.Kiosk"/> key. Wall- and
    /// user-scoped keys deliberately return null: a kiosk tablet is its own scope.
    /// </summary>
    Task<Guid?> ValidateKioskAsync(string token, CancellationToken ct = default);
}
