namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Thrown when a TopLogger session cannot be authenticated for a Blocwerk user
/// and cannot be recovered by refreshing: the user has no tokens, or the refresh
/// token was rejected as stale / rotated out. Callers should treat this as a
/// "needs reauth" signal and prompt the user to reconnect TopLogger. Stored
/// tokens are cleared before this is thrown for a rejected refresh token.
/// </summary>
public sealed class TopLoggerAuthException : Exception
{
    public TopLoggerAuthException(Guid userId, string message)
        : base(message)
    {
        UserId = userId;
    }

    public TopLoggerAuthException(Guid userId, string message, Exception innerException)
        : base(message, innerException)
    {
        UserId = userId;
    }

    /// <summary>
    /// The Blocwerk user whose TopLogger session needs to be reconnected.
    /// </summary>
    public Guid UserId { get; }
}
