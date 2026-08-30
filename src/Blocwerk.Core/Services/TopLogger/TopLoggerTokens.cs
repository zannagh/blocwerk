using System.Text.Json.Serialization;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// The pair of TopLogger JWTs plus their expiry timestamps. The access token is
/// sent as the <c>Authorization: Bearer</c> header on API calls; the refresh
/// token mints new access tokens via <c>authSigninRefreshToken</c>.
/// </summary>
public sealed record TopLoggerTokens(
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("accessExpiresAt")] DateTimeOffset? AccessExpiresAt,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("refreshExpiresAt")] DateTimeOffset? RefreshExpiresAt)
{
    /// <summary>
    /// An empty token set with nothing connected.
    /// </summary>
    public static TopLoggerTokens Empty { get; } = new(null, null, null, null);

    /// <summary>
    /// Whether an access token is present and not within ~60 seconds of expiry.
    /// A null expiry is treated as valid (unknown lifetime, assume usable).
    /// </summary>
    [JsonIgnore]
    public bool IsAccessValid =>
        !string.IsNullOrWhiteSpace(AccessToken)
        && (AccessExpiresAt is null
            || AccessExpiresAt.Value - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60));

    /// <summary>
    /// Whether a refresh token is present.
    /// </summary>
    [JsonIgnore]
    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);
}
