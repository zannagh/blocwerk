namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Whether the current request or circuit belongs to a wall-mounted kiosk tablet, and if so which
/// wall and which kiosk API key it registered with.
/// </summary>
/// <remarks>
/// The interface lives in Core (like <c>ITopLoggerTokenStore</c>) so Core can restrict itself
/// without taking a dependency on the HTTP/auth stack; the implementation lives in
/// Blocwerk.Authentication, where the cookies and the DataProtection key ring are.
/// <para>
/// Two sources feed it, and both are read at the one moment they are reliable (see the
/// implementation): the ACTING session's kiosk claims, which travel in the auth cookie and are
/// therefore available wherever the identity itself is, and — while the tablet is anonymous — the
/// separate kiosk DEVICE cookie. That pairing is deliberate: kiosk restriction is derived from the
/// same principal that grants identity, so a session can never resolve a user without also
/// resolving its kiosk scoping.
/// </para>
/// <para>
/// These members answer "what does this session claim to be", not "is the key still valid". Use
/// them to RESTRICT (over-restricting is safe); anything that GRANTS access must re-validate the
/// key against the database first.
/// </para>
/// </remarks>
public interface IKioskContext
{
    /// <summary>True when this request or circuit comes from a registered kiosk tablet.</summary>
    bool IsKiosk { get; }

    /// <summary>The one wall the tablet is registered to, or null when this is not a kiosk.</summary>
    Guid? KioskWallId { get; }

    /// <summary>The <c>ApiKey.Id</c> of the kiosk key the tablet registered with, or null.</summary>
    Guid? KioskApiKeyId { get; }

    /// <summary>
    /// The one wall a visitor may view read-only, from the protected visit cookie made by a one-time visitor link; null
    /// when this is no visit or the visit has ended. Never set for a kiosk tablet or a signed-in session: a visitor is
    /// always anonymous, so every write still refuses them for want of a user.
    /// </summary>
    Guid? VisitorWallId { get; }

    /// <summary>
    /// When the visit ends, or null when this session carries no visit. Stays set after the visit ended (when
    /// <see cref="VisitorWallId"/> is already null), so the database contexts of such a session stay closed to every
    /// wall rather than falling back to the unstamped default.
    /// </summary>
    DateTimeOffset? VisitorExpiresAt { get; }

    /// <summary>
    /// Resolves the kiosk state from the underlying cookies/principal. Idempotent, and cached for
    /// the lifetime of the scope. Call it once at the start of a request and at circuit open so the
    /// synchronous members above are never read before they are populated.
    /// </summary>
    Task InitializeAsync();
}
