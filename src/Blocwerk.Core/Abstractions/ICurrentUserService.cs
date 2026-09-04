using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Abstractions;

public interface ICurrentUserService
{
    Task<User> GetCurrentUserAsync();

    /// <summary>
    /// Looks up a user by id without any current-user context. Returns null when no such user
    /// exists. Used to render another member's public profile fields (display name, role, join date);
    /// callers are responsible for enforcing whether the viewer may see that profile.
    /// </summary>
    Task<User?> GetUserByIdAsync(Guid id);

    /// <summary>
    /// Returns the OAuth provider keys ("github"/"google"/"microsoft") currently linked to the signed-in
    /// user, drawn from their <see cref="UserIdentity"/> rows. Drives the profile's "linked accounts"
    /// list and decides which "Link {provider}" buttons to offer.
    /// </summary>
    Task<IReadOnlyList<string>> GetLinkedProvidersAsync();

    /// <summary>
    /// Drops the per-scope cached user so the next <see cref="GetCurrentUserAsync"/> re-reads from
    /// the database. Call after mutating the current user's own row (e.g. settings changes) so a
    /// follow-up read in the same circuit sees the new values.
    /// </summary>
    void InvalidateCache();

    /// <summary>
    /// Sets (or clears, when <paramref name="wallId"/> is null) the current user's home wall and
    /// invalidates the cached user. When a wall id is given, the current user must be a member of
    /// that wall; otherwise an <see cref="InvalidOperationException"/> is thrown and nothing changes.
    /// </summary>
    Task SetHomeWallAsync(Guid? wallId);

    /// <summary>
    /// Sets the current user's preferred grade scale: true seeds grade inputs with the Fontainebleau
    /// scale, false with the V-Scale. Persisted on the user row and read back via
    /// <see cref="Entities.User.PreferFontGrades"/>. Invalidates the cached user.
    /// </summary>
    Task SetPreferFontGradesAsync(bool preferFont);

    /// <summary>
    /// Sets whether the current user sees the Tools/Guides button in the bottom navigation bar.
    /// Persisted on the user row (<see cref="Entities.User.ShowToolsInNav"/>) and invalidates the
    /// cached user. Kiosk tablets always show Tools regardless of this setting.
    /// </summary>
    Task SetShowToolsInNavAsync(bool show);

    /// <summary>
    /// Enables or disables a single notification type for the current user. The stored value is an
    /// opt-OUT bitmask (<see cref="Entities.User.DisabledNotifications"/>): <paramref name="disabled"/>
    /// true sets the type's bit (opts out), false clears it (opts back in). Invalidates the cached user.
    /// </summary>
    Task SetNotificationDisabledAsync(NotificationType type, bool disabled);

    /// <summary>
    /// Sets the current user's chosen display name. A null/whitespace value clears it, so the UI
    /// falls back to the OAuth-provided name. The value is trimmed and capped at 256 characters.
    /// Invalidates the cached user.
    /// </summary>
    Task SetDisplayNameAsync(string? name);

    /// <summary>
    /// Sets (or clears, when <paramref name="image"/> is null) the current user's avatar image.
    /// The content type must be jpeg, png or webp and the image at most ~4 MB; otherwise an
    /// <see cref="InvalidOperationException"/> is thrown and nothing changes. Invalidates the
    /// cached user.
    /// </summary>
    Task SetAvatarAsync(byte[]? image, string? contentType);

    /// <summary>
    /// Sets (or changes) the current user's password-login username + password. The user must already
    /// be authenticated (this only ever configures a credential on the existing signed-in user — never
    /// a signup). Validates the username's format and case-insensitive uniqueness and the password
    /// length; throws <see cref="InvalidOperationException"/> on failure and changes nothing. Invalidates
    /// the cached user. When the user already has a password, <paramref name="currentPassword"/> must
    /// match the existing one (step-up re-auth); a first-time set ignores it.
    /// </summary>
    Task SetPasswordAsync(string loginUsername, string password, string? currentPassword);

    /// <summary>
    /// Begins TOTP enrolment for the signed-in user: generates a fresh secret, persists it in its
    /// encrypted form on the user (leaving <see cref="Entities.User.TotpEnabled"/> false — enrolment is
    /// pending until a code is confirmed), and returns the secret, provisioning URI and QR to display
    /// once. The user must already have a password configured; otherwise an
    /// <see cref="InvalidOperationException"/> is thrown. Invalidates the cached user.
    /// </summary>
    Task<TotpEnrollment> BeginTotpEnrollmentAsync();

    /// <summary>
    /// Confirms a pending TOTP enrolment by verifying <paramref name="code"/> against the stored
    /// (decrypted) secret. On success sets <see cref="Entities.User.TotpEnabled"/> to true and returns
    /// true; on an invalid code returns false and leaves the enrolment pending. Invalidates the cached
    /// user on success.
    /// </summary>
    Task<bool> ConfirmTotpAsync(string code);

    /// <summary>
    /// Disables TOTP for the signed-in user after verifying <paramref name="code"/> against their current
    /// authenticator (step-up re-auth): clears the stored secret and sets
    /// <see cref="Entities.User.TotpEnabled"/> to false, then returns true. Returns false and changes
    /// nothing when the code is wrong or TOTP is not enabled. Invalidates the cached user on success.
    /// </summary>
    Task<bool> DisableTotpAsync(string code);
}
