using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Abstractions;

public interface ICurrentUserService
{
    Task<User> GetCurrentUserAsync();

    /// <summary>
    /// Drops the per-scope cached user so the next <see cref="GetCurrentUserAsync"/> re-reads from
    /// the database. Call after mutating the current user's own row (e.g. settings changes) so a
    /// follow-up read in the same circuit sees the new values.
    /// </summary>
    void InvalidateCache();
}
