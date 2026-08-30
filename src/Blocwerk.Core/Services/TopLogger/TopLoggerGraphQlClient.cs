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
/// retries the request a single time.
/// </summary>
public sealed class TopLoggerGraphQlClient : ITopLoggerGraphQlClient
{
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
            await SendCoreAsync<TData>(tokens.AccessToken, request, cancellationToken).ConfigureAwait(false);

        bool unauthenticated = status == HttpStatusCode.Unauthorized || HasUnauthenticatedError(response);
        if (unauthenticated)
        {
            // RefreshAsync throws TopLoggerAuthException on a stale refresh token;
            // that propagates so the caller can flag "needs reauth".
            TopLoggerTokens refreshed = await authService.RefreshAsync(userId, cancellationToken).ConfigureAwait(false);
            (status, response) = await SendCoreAsync<TData>(refreshed.AccessToken, request, cancellationToken)
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

    private async Task<(HttpStatusCode Status, GraphQlResponse<TData> Response)> SendCoreAsync<TData>(
        string? accessToken,
        GraphQlRequest request,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage httpRequest = BuildRequest(accessToken, request);
        using HttpResponseMessage httpResponse =
            await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        GraphQlResponse<TData> response;
        if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
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

        return (httpResponse.StatusCode, response);
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
