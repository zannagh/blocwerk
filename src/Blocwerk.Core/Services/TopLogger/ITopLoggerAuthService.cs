namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Refreshes and rotates a Blocwerk user's TopLogger tokens.
/// </summary>
public interface ITopLoggerAuthService
{
    /// <summary>
    /// Mints a fresh access token from the user's stored refresh token via
    /// <c>authSigninRefreshToken</c>, persists the rotated pair through the
    /// <see cref="ITopLoggerTokenStore"/> and returns it.
    /// </summary>
    /// <param name="userId">The Blocwerk user whose session to refresh.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The refreshed token pair.</returns>
    /// <exception cref="TopLoggerAuthException">
    /// Thrown when the user has no refresh token, or the refresh token was
    /// rejected as stale / rotated out (in which case the stored tokens are
    /// cleared first). The caller should prompt the user to reconnect.
    /// </exception>
    Task<TopLoggerTokens> RefreshAsync(Guid userId, CancellationToken cancellationToken = default);
}
