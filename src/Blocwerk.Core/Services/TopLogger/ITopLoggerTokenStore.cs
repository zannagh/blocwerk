namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Per-user persistence for a Blocwerk user's TopLogger token pair, keyed by the
/// Blocwerk user's <see cref="Guid"/>. Implementations bridge this to whatever
/// backing store is appropriate (e.g. an encrypted database column); this
/// abstraction deliberately says nothing about how or where tokens are stored.
/// </summary>
public interface ITopLoggerTokenStore
{
    /// <summary>
    /// Loads the stored tokens for the given Blocwerk user, or <c>null</c> when
    /// the user has never connected TopLogger.
    /// </summary>
    Task<TopLoggerTokens?> LoadAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores (or replaces) the token pair for the given Blocwerk user.
    /// </summary>
    Task SaveAsync(Guid userId, TopLoggerTokens tokens, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any stored tokens for the given Blocwerk user. Called when a
    /// refresh token is rejected so the UI can fall back to a reconnect prompt.
    /// </summary>
    Task ClearAsync(Guid userId, CancellationToken cancellationToken = default);
}
