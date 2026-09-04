using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Wraps <see cref="IApiKeyService"/> and refuses to MINT a key — or to WIDEN a kiosk key's
/// anonymous-setting permission, or to touch an INSTALLATION key at all — while the session belongs
/// to a kiosk tablet, whatever authority the acting user otherwise has. Narrowing that permission
/// stays allowed.
/// </summary>
/// <remarks>
/// A kiosk session keeps the picked user's full authority over the wall (that is a locked product
/// decision), but minting an API key is different in kind: the key outlives the 30-minute session,
/// survives walking away from the tablet, and is displayed exactly once — on a screen bolted to a
/// public gym wall. So it is blocked on the SERVICE, not merely hidden in the UI: the pages that
/// mint keys are interactive Blazor components that call straight into this service inside the
/// circuit, where no route middleware ever runs.
/// <para>
/// Reads and de-escalations are left alone FOR WALL, KIOSK AND USER KEYS: revoking one, and
/// switching a key's anonymous-setting flag OFF, only ever take permission away, and a kiosk key
/// panel that could not list keys would be confusing without being any safer.
/// </para>
/// <para>
/// <b>Installation keys are the exception, on both counts.</b> They are the app-admin surface, not
/// the wall's, and the policy that fronts that surface —
/// <c>BlocwerkPolicies.AppAdmin</c> — is <c>AppAdminRequirement</c> AND <c>KioskRouteRequirement</c>,
/// while <see cref="AppAdminGuard"/> underneath is the role half only. Leaving listing and revoking
/// unguarded here meant the service and the policy disagreed about the kiosk axis: an app admin
/// acting through a gym tablet could enumerate the installation's credentials and retire the deploy
/// hook's key, silently killing the "server is updating" notice for everybody. Not reachable today
/// (<c>/administration</c> is on the kiosk denied list), which is exactly why it had to be closed
/// here rather than relied on there — the whole point of a service guard is that it does not trust
/// the page in front of it.
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

    public Task<(ApiKey Key, string Token)> CreateInstallationKeyAsync(
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        // The widest key the app can mint, from the least trusted screen it owns. Guarded for the
        // same reason as every other mint, only more so: this one is not scoped to the wall the
        // tablet is bolted to, so it would survive walking out of the gym with it.
        EnsureNotKiosk();
        return inner.CreateInstallationKeyAsync(actingUserId, name, expiresAt, ct);
    }

    public Task<IReadOnlyList<ApiKey>> GetWallKeysAsync(Guid wallId, Guid actingUserId, CancellationToken ct = default)
    {
        return inner.GetWallKeysAsync(wallId, actingUserId, ct);
    }

    public Task<IReadOnlyList<ApiKey>> GetUserKeysAsync(Guid userId, Guid actingUserId, CancellationToken ct = default)
    {
        return inner.GetUserKeysAsync(userId, actingUserId, ct);
    }

    public Task<IReadOnlyList<ApiKey>> GetInstallationKeysAsync(Guid actingUserId, CancellationToken ct = default)
    {
        // Guarded, unlike the wall and user listings, because this is not a listing of the kiosk's
        // own surface: it enumerates the credentials that act for the WHOLE installation, and it
        // belongs to the app-admin dashboard whose policy already refuses a kiosk session. Matching
        // that here is what stops the service being the weaker of the two.
        EnsureNotKiosk("Installation API keys cannot be listed from a kiosk session.");
        return inner.GetInstallationKeysAsync(actingUserId, ct);
    }

    public async Task RevokeAsync(Guid apiKeyId, Guid actingUserId, CancellationToken ct = default)
    {
        // Revoking stays unguarded for wall, kiosk and user keys — it is de-escalation, and an
        // admin standing at the tablet has to be able to switch that very tablet off. An
        // INSTALLATION key is not that: the only one in production is the deploy hook's, retiring it
        // takes a capability away from the SERVER rather than from the tablet, and it would leave
        // nothing on screen to explain why deploys stopped announcing themselves.
        if (kioskContext.IsKiosk && await IsInstallationKeyAsync(apiKeyId, actingUserId, ct))
        {
            throw new KioskRestrictedException("Installation API keys cannot be revoked from a kiosk session.");
        }

        await inner.RevokeAsync(apiKeyId, actingUserId, ct);
    }

    /// <summary>
    /// Whether <paramref name="apiKeyId"/> is installation-scoped, answered through the listing the
    /// acting user is already entitled to.
    /// </summary>
    /// <remarks>
    /// Deliberately no new <see cref="IApiKeyService"/> member for this. Only an app administrator
    /// may list — or revoke — an installation key at all, so a caller the listing refuses is a
    /// caller <see cref="IApiKeyService.RevokeAsync"/> is about to refuse anyway, and answering
    /// "not an installation key" for them changes nothing except which exception they see. The
    /// extra query costs one read, and only inside a kiosk session.
    /// </remarks>
    private async Task<bool> IsInstallationKeyAsync(Guid apiKeyId, Guid actingUserId, CancellationToken ct)
    {
        IReadOnlyList<ApiKey> installationKeys;
        try
        {
            installationKeys = await inner.GetInstallationKeysAsync(actingUserId, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return installationKeys.Any(k => k.Id == apiKeyId);
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
