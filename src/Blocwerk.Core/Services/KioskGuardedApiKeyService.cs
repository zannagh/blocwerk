using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Wraps <see cref="IApiKeyService"/> and refuses to MINT a key while the session belongs to a kiosk
/// tablet, whatever authority the acting user otherwise has.
/// </summary>
/// <remarks>
/// A kiosk session keeps the picked user's full authority over the wall (that is a locked product
/// decision), but minting an API key is different in kind: the key outlives the 30-minute session,
/// survives walking away from the tablet, and is displayed exactly once — on a screen bolted to a
/// public gym wall. So it is blocked on the SERVICE, not merely hidden in the UI: the pages that
/// mint keys are interactive Blazor components that call straight into this service inside the
/// circuit, where no route middleware ever runs.
/// <para>
/// Reads and revocations are left alone. Revoking is a de-escalation, and a kiosk key panel that
/// could not list keys would be confusing without being any safer.
/// </para>
/// </remarks>
public sealed class KioskGuardedApiKeyService : IApiKeyService
{
    private readonly IApiKeyService inner;
    private readonly IKioskContext kioskContext;

    public KioskGuardedApiKeyService(IApiKeyService inner, IKioskContext kioskContext)
    {
        this.inner = inner;
        this.kioskContext = kioskContext;
    }

    public Task<(ApiKey Key, string Token)> CreateWallKeyAsync(
        Guid wallId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        EnsureNotKiosk();
        return inner.CreateWallKeyAsync(wallId, actingUserId, name, expiresAt, ct);
    }

    public Task<(ApiKey Key, string Token)> CreateKioskKeyAsync(
        Guid wallId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        EnsureNotKiosk();
        return inner.CreateKioskKeyAsync(wallId, actingUserId, name, expiresAt, ct);
    }

    public Task<(ApiKey Key, string Token)> CreateUserKeyAsync(
        Guid userId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        EnsureNotKiosk();
        return inner.CreateUserKeyAsync(userId, actingUserId, name, expiresAt, ct);
    }

    public Task<IReadOnlyList<ApiKey>> GetWallKeysAsync(Guid wallId, Guid actingUserId, CancellationToken ct = default)
    {
        return inner.GetWallKeysAsync(wallId, actingUserId, ct);
    }

    public Task<IReadOnlyList<ApiKey>> GetUserKeysAsync(Guid userId, Guid actingUserId, CancellationToken ct = default)
    {
        return inner.GetUserKeysAsync(userId, actingUserId, ct);
    }

    public Task RevokeAsync(Guid apiKeyId, Guid actingUserId, CancellationToken ct = default)
    {
        return inner.RevokeAsync(apiKeyId, actingUserId, ct);
    }

    public Task<ApiKey?> ValidateAsync(string token, CancellationToken ct = default)
    {
        return inner.ValidateAsync(token, ct);
    }

    public Task<Guid?> ValidateKioskAsync(string token, CancellationToken ct = default)
    {
        // Never guarded: this is the call that REGISTERS a tablet, made while the request is still
        // anonymous. Guarding it would make kiosk registration impossible from a kiosk.
        return inner.ValidateKioskAsync(token, ct);
    }

    private void EnsureNotKiosk()
    {
        if (kioskContext.IsKiosk)
        {
            throw new KioskRestrictedException("API keys cannot be created from a kiosk session.");
        }
    }
}
