namespace Blocwerk.Core.Services.TopLogger;

/// <summary>Thrown when a stored token is rejected (HTTP 401) and the user must reconnect.</summary>
public sealed class TopLoggerAuthException : Exception
{
    public TopLoggerAuthException(string message)
        : base(message)
    {
    }
}
