namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Thrown when TopLogger keeps rate-limiting a request (HTTP 429 or a
/// throttling GraphQL error) after the client has exhausted its backoff retries.
/// This propagates so a partial pull is never committed as a success: the import
/// service records it as a failure and the run can be retried later.
/// </summary>
public sealed class TopLoggerThrottledException : Exception
{
    public TopLoggerThrottledException(string message)
        : base(message)
    {
    }

    public TopLoggerThrottledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
