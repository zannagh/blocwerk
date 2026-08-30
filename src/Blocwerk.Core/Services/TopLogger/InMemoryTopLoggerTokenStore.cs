using System.Collections.Concurrent;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// A trivial, process-local <see cref="ITopLoggerTokenStore"/> backed by a
/// concurrent dictionary. Intended for tests and local development only; it does
/// not persist across restarts and performs no encryption. Production code
/// should supply a store bridged to the encrypted database.
/// </summary>
public sealed class InMemoryTopLoggerTokenStore : ITopLoggerTokenStore
{
    private readonly ConcurrentDictionary<Guid, TopLoggerTokens> store = new();

    /// <inheritdoc />
    public Task<TopLoggerTokens?> LoadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(store.TryGetValue(userId, out TopLoggerTokens? value) ? value : null);
    }

    /// <inheritdoc />
    public Task SaveAsync(Guid userId, TopLoggerTokens tokens, CancellationToken cancellationToken = default)
    {
        store[userId] = tokens;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        store.TryRemove(userId, out _);
        return Task.CompletedTask;
    }
}
