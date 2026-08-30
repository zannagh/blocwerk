namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Strongly typed configuration for the vendored TopLogger GraphQL client.
/// Bound from configuration and registered as a singleton (no <c>IOptions</c>
/// indirection). Tokens are not held here — they live per-user in the
/// <see cref="ITopLoggerTokenStore"/>.
/// </summary>
public sealed class TopLoggerSettings
{
    /// <summary>
    /// The GraphQL endpoint the client posts operations to.
    /// </summary>
    public string GraphQlUrl { get; set; } = "https://api.toplogger.nu/graphql";

    /// <summary>
    /// The browser-like User-Agent sent with every request.
    /// </summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 " +
        "(KHTML, like Gecko) Version/17.0 Safari/605.1.15";

    /// <summary>
    /// Minimum interval enforced between two outgoing requests.
    /// </summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Maximum random jitter added on top of <see cref="MinRequestInterval"/>.
    /// </summary>
    public TimeSpan MaxJitter { get; set; } = TimeSpan.FromMilliseconds(750);
}
