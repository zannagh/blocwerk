namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// A GraphQL client for the TopLogger API that attaches a Blocwerk user's access
/// token as the Bearer header and, on an <c>UNAUTHENTICATED</c> / HTTP 401
/// response, refreshes that user's token once and retries the request a single
/// time. Operations are keyed by the Blocwerk user's <see cref="Guid"/>.
/// </summary>
public interface ITopLoggerGraphQlClient
{
    /// <summary>
    /// Sends a GraphQL request on behalf of the given Blocwerk user.
    /// </summary>
    /// <exception cref="TopLoggerAuthException">
    /// Thrown when the user is not connected or the session cannot be refreshed.
    /// </exception>
    Task<GraphQlResponse<TData>> SendAsync<TData>(
        Guid userId,
        GraphQlRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a GraphQL operation on behalf of the given Blocwerk user.
    /// </summary>
    /// <exception cref="TopLoggerAuthException">
    /// Thrown when the user is not connected or the session cannot be refreshed.
    /// </exception>
    Task<GraphQlResponse<TData>> SendAsync<TData>(
        Guid userId,
        string operationName,
        string query,
        object? variables = null,
        CancellationToken cancellationToken = default);
}
