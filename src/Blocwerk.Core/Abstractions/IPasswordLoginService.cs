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
}
