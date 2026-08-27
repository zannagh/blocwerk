using Blocwerk.Core.Entities;

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
}
