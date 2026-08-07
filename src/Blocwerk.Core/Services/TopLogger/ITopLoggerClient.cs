namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Talks to TopLogger's (unofficial) API. Abstracts the two backends — legacy REST at
/// api.toplogger.nu/v1 and the newer GraphQL platform — behind one surface; the implementation
/// probes both on sign-in and records which one authenticated.
/// </summary>
public interface ITopLoggerClient
{
    /// <summary>Authenticates with email + password, returning a token, the user id, and the backend.</summary>
    Task<TopLoggerAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Fetches the user's ascents, optionally only those logged at/after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<TopLoggerAscentDto>> GetAscentsAsync(
        TopLoggerCredentials credentials, DateTimeOffset? since, CancellationToken cancellationToken = default);
}
