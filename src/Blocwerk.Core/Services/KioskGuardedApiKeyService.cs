using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Wraps <see cref="IApiKeyService"/> and refuses to MINT a key — or to WIDEN a kiosk key's
/// anonymous-setting permission — while the session belongs to a kiosk tablet, whatever authority
/// the acting user otherwise has. Narrowing that permission stays allowed.
/// </summary>
/// <remarks>
/// A kiosk session keeps the picked user's full authority over the wall (that is a locked product
/// decision), but minting an API key is different in kind: the key outlives the 30-minute session,
/// survives walking away from the tablet, and is displayed exactly once — on a screen bolted to a
/// public gym wall. So it is blocked on the SERVICE, not merely hidden in the UI: the pages that
/// mint keys are interactive Blazor components that call straight into this service inside the
/// circuit, where no route middleware ever runs.
/// <para>
/// Reads and de-escalations are left alone: revoking a key, and switching a key's anonymous-setting
/// flag OFF, only ever take permission away, and a kiosk key panel that could not list keys would be
/// confusing without being any safer.
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

    public Task SetAnonymousKioskSettingAsync(
        Guid apiKeyId,
        Guid actingUserId,
        bool allowed,
        CancellationToken ct = default)
    {
        // Asymmetric on purpose, and the direction is the whole point. Turning the flag ON widens
        // what a tablet may do without anybody signed in, so a kiosk session must not be able to do
        // it — that is self-escalation, exactly the thing minting is blocked for. Turning it OFF
        // only takes permission away, so it is allowed from a kiosk for the same reason
        // <see cref="RevokeAsync"/> is left unguarded: an admin standing at the tablet has to be
        // able to switch that very tablet off, and refusing them would be security theatre that
        // hands them a generic error banner instead.
        if (allowed)
        {
            EnsureNotKiosk("Anonymous setting cannot be switched on from a kiosk session.");
        }

        return inner.SetAnonymousKioskSettingAsync(apiKeyId, actingUserId, allowed, ct);
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

    private void EnsureNotKiosk(
        string message = "API keys cannot be created from a kiosk session.")
    {
        if (kioskContext.IsKiosk)
        {
            throw new KioskRestrictedException(message);
        }
    }
}
