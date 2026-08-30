using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Default <see cref="ITopLoggerAuthService"/>. Refreshes access tokens via a
/// direct, self-contained call in which the refresh token doubles as the Bearer
/// header, then rotates the stored pair through the per-user
/// <see cref="ITopLoggerTokenStore"/>. A rejected refresh token is surfaced as a
/// <see cref="TopLoggerAuthException"/> so the caller can flag "needs reauth".
/// </summary>
public sealed class TopLoggerAuthService : ITopLoggerAuthService
{
    /// <summary>
    /// Name of the named <see cref="HttpClient"/> used for the direct refresh
    /// call (browser User-Agent; the refresh token is sent as the Bearer header).
    /// </summary>
    public const string RefreshHttpClientName = "toplogger-auth";

    private const string RefreshMutation =
        "mutation authSigninRefreshToken($refreshToken: JWT!) { " +
        "tokens: authSigninRefreshToken(refreshToken: $refreshToken) { " +
        "access { token expiresAt } refresh { token expiresAt } } }";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITopLoggerTokenStore tokenStore;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly TopLoggerSettings settings;
    private readonly ILogger<TopLoggerAuthService> logger;

    public TopLoggerAuthService(
        ITopLoggerTokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        TopLoggerSettings settings,
        ILogger<TopLoggerAuthService> logger)
    {
        this.tokenStore = tokenStore;
        this.httpClientFactory = httpClientFactory;
        this.settings = settings;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<TopLoggerTokens> RefreshAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        TopLoggerTokens? existing = await tokenStore.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        string? refreshToken = existing?.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new TopLoggerAuthException(userId, "No TopLogger refresh token is stored for the user.");
        }

        RefreshResult result;
        try
        {
            result = await PostRefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new TopLoggerAuthException(userId, "The TopLogger token refresh call failed.", ex);
        }

        // A stale / rotated-out refresh token can never recover: clear the stored
        // tokens so the UI falls back to the connect prompt, and surface the
        // failure distinctly instead of silently swallowing it.
        if (result.AuthFailed)
        {
            logger.LogWarning("TopLogger refresh token for user {UserId} was rejected as stale; clearing.", userId);
            await tokenStore.ClearAsync(userId, cancellationToken).ConfigureAwait(false);
            throw new TopLoggerAuthException(userId, "The TopLogger refresh token was rejected. Reconnect required.");
        }

        RefreshTokens? tokens = result.Tokens;
        if (tokens?.Access is null || string.IsNullOrWhiteSpace(tokens.Access.Token))
        {
            throw new TopLoggerAuthException(userId, "The TopLogger token refresh returned no access token.");
        }

        TopLoggerTokens rotated = new(
            tokens.Access.Token,
            tokens.Access.ExpiresAt,
            string.IsNullOrWhiteSpace(tokens.Refresh?.Token) ? refreshToken : tokens.Refresh!.Token,
            tokens.Refresh?.ExpiresAt ?? existing!.RefreshExpiresAt);

        await tokenStore.SaveAsync(userId, rotated, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Refreshed TopLogger access token for user {UserId}.", userId);
        return rotated;
    }

    private async Task<RefreshResult> PostRefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        HttpClient http = httpClientFactory.CreateClient(RefreshHttpClientName);
        GraphQlRequest request = new(RefreshMutation, new { refreshToken }, "authSigninRefreshToken");

        using HttpRequestMessage message = new(HttpMethod.Post, settings.GraphQlUrl)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);
        if (message.Headers.UserAgent.Count == 0 && !string.IsNullOrWhiteSpace(settings.UserAgent))
        {
            message.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent);
        }

        using HttpResponseMessage response = await http
            .SendAsync(message, cancellationToken)
            .ConfigureAwait(false);

        // A 401 means the refresh token itself is no longer accepted.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new RefreshResult(null, true);
        }

        response.EnsureSuccessStatusCode();

        GraphQlResponse<RefreshData>? parsed = await response.Content
            .ReadFromJsonAsync<GraphQlResponse<RefreshData>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        // A 200 body carrying an UNAUTHENTICATED GraphQL error is the same signal.
        if (parsed is { HasErrors: true } && UnauthenticatedDetector.IsUnauthenticated(parsed.Errors))
        {
            return new RefreshResult(null, true);
        }

        return new RefreshResult(parsed?.Data?.Tokens, false);
    }
}
