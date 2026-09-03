namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Re-checks a kiosk registration against the database: the key still exists, is unrevoked,
/// unexpired, still kiosk-scoped, and still belongs to the wall the device cookie claims.
/// </summary>
/// <remarks>
/// The interface lives in Core (like <see cref="IKioskContext"/>) so a Core service can perform the
/// GRANTING half of a kiosk check without depending on the HTTP/auth stack; the implementation is
/// <c>Blocwerk.Authentication.Kiosk.KioskKeyValidator</c>, next to the cookies and the key ring.
/// <para>
/// <see cref="IKioskContext"/> answers "what does this session claim to be" and is only safe to
/// RESTRICT on. This is what anything that GRANTS must call as well, so that revoking a kiosk key
/// turns off every tablet using it rather than only the ones that lost their cookie.
/// </para>
/// </remarks>
public interface IKioskKeyValidator
{
    /// <summary>True while this kiosk key is still live for this wall.</summary>
    Task<bool> IsKeyValidAsync(Guid apiKeyId, Guid wallId, CancellationToken ct = default);

    /// <summary>True while the user still consents to being picked at this wall's kiosk.</summary>
    Task<bool> HasConsentAsync(Guid wallId, Guid userId, CancellationToken ct = default);
}
