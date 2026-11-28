// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Authentication.Apple;

/// <summary>Trades an Apple authorization code for its identity token at Apple's token endpoint.</summary>
public sealed class AppleTokenClient(
    IHttpClientFactory httpClients,
    AppleWebSettings settings,
    AppleClientSecret clientSecret,
    ILogger<AppleTokenClient> logger)
{
    /// <summary>The named HTTP client the exchange uses.</summary>
    public const string HttpClientName = "apple-web";

    /// <summary>
    /// The identity token for <paramref name="code"/>, or null when Apple refuses the code or the provider has no key.
    /// <paramref name="redirectUri"/> must be the one the authorize request named.
    /// </summary>
    public async Task<string?> ExchangeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var secret = clientSecret.Get();
        if (secret is null)
        {
            return null;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ServicesId,
            ["client_secret"] = secret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        });

        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.PostAsync(settings.TokenUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Apple token exchange refused with status {Status}", (int)response.StatusCode);
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("id_token", out var idToken) && idToken.ValueKind == JsonValueKind.String
                ? idToken.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("Apple token exchange failed: {Reason}", ex.GetType().Name);
            return null;
        }
    }
}
