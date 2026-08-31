using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Default <see cref="ITopLoggerGraphQlClient"/> backed by a typed
/// <see cref="HttpClient"/> (paced via <see cref="PacingHandler"/>). Attaches the
/// user's current access token and, on an <c>UNAUTHENTICATED</c> / HTTP 401
/// response, refreshes once through <see cref="ITopLoggerAuthService"/> and
/// retries the request a single time. Separately, on a throttle (HTTP 429 or a
/// throttling GraphQL error) it retries the same request with exponential backoff
/// and throws <see cref="TopLoggerThrottledException"/> if still throttled after
/// the attempts are exhausted, so a caller never mistakes a rate-limit for empty data.
/// </summary>
public sealed class TopLoggerGraphQlClient : ITopLoggerGraphQlClient
{
    // Bounded exponential backoff for throttling: up to 4 retries at ~1s, 2s, 4s, 8s.
    private const int MaxThrottleRetries = 4;
    private static readonly TimeSpan ThrottleBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ThrottleJitter = TimeSpan.FromMilliseconds(250);

    // Upper bound on a single backoff wait, so an oversized Retry-After can't stall the pull.
    private static readonly TimeSpan MaxThrottleDelay = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };


    private readonly HttpClient httpClient;
    private readonly TopLoggerSettings settings;
    private readonly ITopLoggerTokenStore tokenStore;
    private readonly ITopLoggerAuthService authService;
    private readonly ILogger<TopLoggerGraphQlClient> logger;

    public TopLoggerGraphQlClient(
        HttpClient httpClient,
        TopLoggerSettings settings,
        ITopLoggerTokenStore tokenStore,
        ITopLoggerAuthService authService,
        ILogger<TopLoggerGraphQlClient> logger)
    {
        this.httpClient = httpClient;
        this.settings = settings;
        this.tokenStore = tokenStore;
        this.authService = authService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<GraphQlResponse<TData>> SendAsync<TData>(
        Guid userId,
        GraphQlRequest request,
        CancellationToken cancellationToken = default)
    {
        TopLoggerTokens tokens = await EnsureAccessAsync(userId, cancellationToken).ConfigureAwait(false);

        (HttpStatusCode status, GraphQlResponse<TData> response) =
            await SendWithThrottleRetryAsync<TData>(tokens.AccessToken, request, cancellationToken).ConfigureAwait(false);

        bool unauthenticated = status == HttpStatusCode.Unauthorized || HasUnauthenticatedError(response);
        if (unauthenticated)
        {
            // RefreshAsync throws TopLoggerAuthException on a stale refresh token;
            // that propagates so the caller can flag "needs reauth". The throttle
            // backoff is separate and applies to this retried send too.
            TopLoggerTokens refreshed = await authService.RefreshAsync(userId, cancellationToken).ConfigureAwait(false);
            (status, response) = await SendWithThrottleRetryAsync<TData>(refreshed.AccessToken, request, cancellationToken)
                .ConfigureAwait(false);
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            throw new TopLoggerAuthException(userId, "TopLogger rejected the request as unauthenticated.");
        }

        LogErrors(request.OperationName, response);
        return response;
    }

    /// <inheritdoc />
    public Task<GraphQlResponse<TData>> SendAsync<TData>(
        Guid userId,
        string operationName,
        string query,
        object? variables = null,
        CancellationToken cancellationToken = default)
    {
        GraphQlRequest request = new(query, variables, operationName);
        return SendAsync<TData>(userId, request, cancellationToken);
    }

    private async Task<TopLoggerTokens> EnsureAccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        TopLoggerTokens? tokens = await tokenStore.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (tokens is null || (!tokens.IsAccessValid && !tokens.HasRefreshToken))
        {
            throw new TopLoggerAuthException(userId, "No usable TopLogger token is stored for the user.");
        }

        if (tokens.IsAccessValid)
        {
            return tokens;
        }

        // Access token is missing/expired but a refresh token is present: refresh
        // proactively (throws TopLoggerAuthException if the refresh token is dead).
        return await authService.RefreshAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the request, retrying the SAME request with bounded exponential
    /// backoff while TopLogger reports throttling (HTTP 429 or a throttling GraphQL
    /// error). Honors a <c>Retry-After</c> header when present. Throws
    /// <see cref="TopLoggerThrottledException"/> once the retries are exhausted so a
    /// persistent throttle surfaces as a failure rather than as silently empty data.
    /// </summary>
    private async Task<(HttpStatusCode Status, GraphQlResponse<TData> Response)> SendWithThrottleRetryAsync<TData>(
        string? accessToken,
        GraphQlRequest request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            (HttpStatusCode status, TimeSpan? retryAfter, GraphQlResponse<TData> response) =
                await SendCoreAsync<TData>(accessToken, request, cancellationToken).ConfigureAwait(false);

            bool throttled = ThrottleDetector.IsThrottled(status) || ThrottleDetector.IsThrottled(response.Errors);
            if (!throttled)
            {
                return (status, response);
            }

            if (attempt >= MaxThrottleRetries)
            {
                throw new TopLoggerThrottledException(
                    $"TopLogger kept rate-limiting '{request.OperationName ?? "(anonymous)"}' after "
                    + $"{MaxThrottleRetries} retries.");
            }

            TimeSpan delay = ComputeBackoff(attempt, retryAfter);
            logger.LogWarning(
                "TopLogger throttled operation {Operation} (attempt {Attempt}); backing off {DelayMs} ms.",
                request.OperationName ?? "(anonymous)",
                attempt + 1,
                delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan ComputeBackoff(int attempt, TimeSpan? retryAfter)
    {
        // Honor an explicit Retry-After when the server sends one; otherwise use
        // exponential backoff (~1s, 2s, 4s, 8s) with a small fixed jitter.
        if (retryAfter is { } hinted && hinted > TimeSpan.Zero)
        {
            return hinted < MaxThrottleDelay ? hinted : MaxThrottleDelay;
        }

        double baseMs = ThrottleBaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        double jitterMs = Random.Shared.NextDouble() * ThrottleJitter.TotalMilliseconds;
        TimeSpan computed = TimeSpan.FromMilliseconds(baseMs + jitterMs);
        return computed < MaxThrottleDelay ? computed : MaxThrottleDelay;
    }

    private async Task<(HttpStatusCode Status, TimeSpan? RetryAfter, GraphQlResponse<TData> Response)> SendCoreAsync<TData>(
        string? accessToken,
        GraphQlRequest request,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage httpRequest = BuildRequest(accessToken, request);
        using HttpResponseMessage httpResponse =
            await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        TimeSpan? retryAfter = ReadRetryAfter(httpResponse);

        GraphQlResponse<TData> response;
        if (httpResponse.StatusCode == HttpStatusCode.Unauthorized
            || httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 401 is handled by the refresh path; 429 by the throttle backoff. In
            // both cases the body is not the GraphQL payload, so it is not parsed.
            response = new GraphQlResponse<TData>(default, null);
        }
        else
        {
            httpResponse.EnsureSuccessStatusCode();
            GraphQlResponse<TData>? parsed = await httpResponse.Content
                .ReadFromJsonAsync<GraphQlResponse<TData>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            response = parsed ?? new GraphQlResponse<TData>(default, null);
        }

        return (httpResponse.StatusCode, retryAfter, response);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage httpResponse)
    {
        RetryConditionHeaderValue? header = httpResponse.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            TimeSpan until = date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }

        return null;
    }

    private HttpRequestMessage BuildRequest(string? accessToken, GraphQlRequest request)
    {
        HttpRequestMessage httpRequest = new(HttpMethod.Post, settings.GraphQlUrl)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return httpRequest;
    }

    private static bool HasUnauthenticatedError<TData>(GraphQlResponse<TData> response)
    {
        return UnauthenticatedDetector.IsUnauthenticated(response.Errors);
    }

    private void LogErrors<TData>(string? operationName, GraphQlResponse<TData> response)
    {
        if (!response.HasErrors || response.Errors is null)
        {
            return;
        }

        foreach (GraphQlError error in response.Errors)
        {
            logger.LogError(
                "TopLogger GraphQL operation {Operation} returned error: {Message}",
                operationName ?? "(anonymous)",
                error.Message);
        }
    }
}
