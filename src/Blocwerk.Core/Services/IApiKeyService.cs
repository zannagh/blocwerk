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

    /// <summary>Issues a key scoped to the user's own access.</summary>
    Task<(ApiKey Key, string Token)> CreateUserKeyAsync(
        Guid userId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    /// <summary>All keys of a wall, newest first. Revoked and expired keys are included.</summary>
    /// <exception cref="UnauthorizedAccessException">The acting user does not administer the wall.</exception>
    Task<IReadOnlyList<ApiKey>> GetWallKeysAsync(Guid wallId, Guid actingUserId, CancellationToken ct = default);

    /// <summary>All user-scoped keys of a user, newest first. Revoked and expired keys are included.</summary>
    Task<IReadOnlyList<ApiKey>> GetUserKeysAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Revokes a key. The acting user must own it (user scope) or administer its wall (wall scope).
    /// Revoking an already revoked key is a no-op.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user may not revoke the key.</exception>
    Task RevokeAsync(Guid apiKeyId, Guid actingUserId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a bearer token to its key, or null when it is unknown, revoked or expired.
    /// Stamps <see cref="ApiKey.LastUsedAt"/>, throttled to at most one write per minute.
    /// </summary>
    Task<ApiKey?> ValidateAsync(string token, CancellationToken ct = default);
}
