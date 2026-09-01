namespace Blocwerk.Core.Services;

/// <summary>
/// Kiosk mode: a wall-mounted tablet browses one wall anonymously and offers a picker of the wall
/// members who have opted in to being acted as. This service owns the opt-in (consent) and the
/// optional PIN that guards a pick.
/// </summary>
/// <remarks>
/// The methods split into two groups by who authorises them:
/// <list type="bullet">
/// <item><description>
/// <see cref="ConsentAsync"/>, <see cref="RevokeConsentAsync"/> and <see cref="HasConsentedAsync"/>
/// act on the <b>signed-in</b> user and resolve them through <c>ICurrentUserService</c>. A member can
/// only ever grant or revoke their own consent — there is no path for one user to consent for another.
/// </description></item>
/// <item><description>
/// <see cref="GetConsentingUsersAsync"/> and <see cref="VerifyPinAsync"/> run on behalf of the
/// <b>anonymous</b> tablet, before anyone has been picked, so they must not require a signed-in user.
/// They perform <b>no</b> authorisation of their own: proving that the caller may act on that wall —
/// in practice validating a kiosk API key and using the wall id it carries — is the caller's job.
/// </description></item>
/// </list>
/// </remarks>
public interface IKioskService
{
    /// <summary>
    /// Records the <b>current</b> user's consent to appear in the given wall's kiosk picker, optionally
    /// guarding the pick with a short PIN. Passing a null/empty <paramref name="pin"/> clears any PIN
    /// previously set. Re-consenting refreshes the timestamp and replaces the PIN.
    /// </summary>
    /// <param name="wallId">The wall to consent for; the current user must already be a member of it.</param>
    /// <param name="pin">An optional PIN of 4 to 8 digits, stored only as a salted hash.</param>
    /// <exception cref="UnauthorizedAccessException">No user is signed in.</exception>
    /// <exception cref="InvalidOperationException">
    /// The current user is not a member of the wall, or the PIN is not 4 to 8 digits.
    /// </exception>
    Task ConsentAsync(Guid wallId, string? pin);

    /// <summary>
    /// Withdraws the <b>current</b> user's kiosk consent for the wall, clearing both the consent
    /// timestamp and any stored PIN hash. A no-op when they had not consented.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">No user is signed in.</exception>
    Task RevokeConsentAsync(Guid wallId);

    /// <summary>
    /// Whether the <b>current</b> user currently consents to kiosk use of the wall. Drives the toggle
    /// state in the member's own settings; false when they are not a member at all.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">No user is signed in.</exception>
    Task<bool> HasConsentedAsync(Guid wallId);

    /// <summary>
    /// The wall's kiosk picker list: every member of <paramref name="wallId"/> who has consented,
    /// ordered by name.
    /// </summary>
    /// <remarks>
    /// <b>Callable without an authenticated user</b> — the tablet is anonymous while the picker is on
    /// screen — and therefore <b>performs no authorisation</b>. The caller must have already proven that
    /// it may act on this wall (a validated kiosk API key whose wall id it passes here); this method
    /// trusts <paramref name="wallId"/> completely.
    /// </remarks>
    Task<IReadOnlyList<KioskUserInfo>> GetConsentingUsersAsync(Guid wallId);

    /// <summary>
    /// Whether a kiosk pick of <paramref name="userId"/> on <paramref name="wallId"/> should be allowed:
    /// true when that member has consented and either has no PIN set and <paramref name="pin"/> is
    /// null/empty, or the PIN matches the stored hash. False in every other case — a non-member,
    /// a member who never consented, a wrong PIN, a missing PIN where one is required, and a PIN
    /// supplied where none is set all look identical to the caller.
    /// </summary>
    /// <remarks>
    /// <b>Callable without an authenticated user</b>, and like
    /// <see cref="GetConsentingUsersAsync"/> it performs <b>no authorisation of its own</b> — the caller
    /// must already have proven kiosk access to that wall.
    /// </remarks>
    Task<bool> VerifyPinAsync(Guid wallId, Guid userId, string? pin);
}
