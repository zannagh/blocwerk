using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Manages the login-username + password credential on an EXISTING user and looks users up by that
/// username for authentication. This is never a signup path: <see cref="SetPasswordAsync"/> requires
/// an existing user id and <see cref="FindByLoginUsernameAsync"/> only ever reads, so a password
/// login can neither create a user nor reveal whether a username exists to the caller.
/// </summary>
public interface IPasswordLoginService
{
    /// <summary>
    /// Sets (or changes) the login username + password for the existing user with
    /// <paramref name="userId"/>. Validates the username (3–64 chars, letters/digits/._-) and its
    /// case-insensitive uniqueness across all other users, and the password (min 8 chars), then
    /// stores a salted hash. When the user ALREADY has a password, <paramref name="currentPassword"/>
    /// must match it (step-up re-auth); a first-time set (no existing password) ignores it. Throws
    /// <see cref="InvalidOperationException"/> on any validation failure (invalid username, taken
    /// username, short password, wrong current password, or unknown user) and changes nothing.
    /// </summary>
    Task SetPasswordAsync(Guid userId, string loginUsername, string password, string? currentPassword);

    /// <summary>
    /// Finds the single user whose <see cref="User.LoginUsername"/> matches
    /// <paramref name="loginUsername"/> case-insensitively AND who has a non-null
    /// <see cref="User.PasswordHash"/>. Returns null when none matches. Read-only: it never creates a
    /// user, which is what keeps password login from becoming a signup path.
    /// </summary>
    Task<User?> FindByLoginUsernameAsync(string loginUsername);

    /// <summary>
    /// True when <paramref name="loginUsername"/> is a well-formed (3–64 chars, letters/digits/._-)
    /// username that no user currently holds (case-insensitively). A UX pre-check only; the DB unique
    /// index remains the race-safe backstop enforced by <see cref="CreateLocalUserAsync"/>.
    /// </summary>
    Task<bool> IsUsernameAvailableAsync(string loginUsername);

    /// <summary>
    /// Finds the single user whose confirmed <see cref="User.Email"/> matches <paramref name="email"/>
    /// (normalized) and whose <see cref="User.EmailVerified"/> is true. Returns null when none matches.
    /// Read-only; used by password recovery to resolve which account a reset code belongs to.
    /// </summary>
    Task<User?> FindByEmailAsync(string email);

    /// <summary>
    /// Creates a brand-new local account (no OAuth identity) from a chosen username + password and a
    /// already-verified email. Validates the inputs, re-checks username/email uniqueness, synthesizes a
    /// unique <see cref="User.Identifier"/>, stores a salted password hash and sets
    /// <see cref="User.EmailVerified"/>. The caller MUST have confirmed the email with a verification
    /// code first — this method trusts <paramref name="email"/> as verified. Race-safe: a uniqueness
    /// collision (DB unique index) is caught and reported as
    /// <see cref="LocalUserCreateStatus.UsernameTaken"/> / <see cref="LocalUserCreateStatus.EmailTaken"/>.
    /// Never attaches to or overwrites an existing user and never grants a role above
    /// <c>IdentityRole.User</c>.
    /// </summary>
    Task<LocalUserCreateResult> CreateLocalUserAsync(string loginUsername, string password, string email);

    /// <summary>
    /// Sets a new password hash on the user with <paramref name="userId"/> WITHOUT requiring the old
    /// password or a step-up — the caller must already have proven control of the account through a
    /// verified password-reset email code. Validates the new password (min 8 chars). Throws
    /// <see cref="InvalidOperationException"/> when the password is too short or the user is unknown.
    /// </summary>
    Task ResetPasswordAsync(Guid userId, string newPassword);
}
