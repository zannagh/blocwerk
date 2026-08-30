using System.Text.Json.Serialization;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// The <c>data</c> payload of the <c>authSigninRefreshToken</c> mutation.
/// </summary>
public sealed record RefreshData(
    [property: JsonPropertyName("tokens")] RefreshTokens? Tokens);

/// <summary>
/// The refreshed access/refresh token pair returned by the mutation.
/// </summary>
public sealed record RefreshTokens(
    [property: JsonPropertyName("access")] RefreshToken? Access,
    [property: JsonPropertyName("refresh")] RefreshToken? Refresh);

/// <summary>
/// A single token plus its expiry as returned by the auth mutations.
/// </summary>
public sealed record RefreshToken(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);

/// <summary>
/// Internal result of a single refresh HTTP call: the rotated tokens, or a flag
/// that the refresh token was rejected as unauthenticated.
/// </summary>
internal sealed record RefreshResult(RefreshTokens? Tokens, bool AuthFailed);
