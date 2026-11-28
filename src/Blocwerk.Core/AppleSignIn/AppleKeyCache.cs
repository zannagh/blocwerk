// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Core.AppleSignIn;

/// <inheritdoc cref="IAppleKeySource"/>
/// <remarks>
/// A singleton. The keys are kept for <see cref="MaxAge"/>; an unknown key id fetches them early, so a key Apple
/// rotated in is picked up at once. Fetches, refreshes included, run one at a time and at most once per
/// <see cref="MinFetchInterval"/>, so a stream of tokens with made-up key ids cannot turn into a stream of requests
/// to Apple. A failed fetch keeps the old keys.
/// </remarks>
public sealed class AppleKeyCache(
    IHttpClientFactory httpClients,
    AppleSignInSettings settings,
    ILogger<AppleKeyCache> logger,
    TimeProvider? clock = null) : IAppleKeySource, IDisposable
{
    /// <summary>The named HTTP client the keys are fetched with.</summary>
    public const string HttpClientName = "apple-signin-keys";

    /// <summary>How long fetched keys are used without asking Apple again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>The shortest time between two fetches, whatever asks for them.</summary>
    public static readonly TimeSpan MinFetchInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<SecurityKey> keys = [];
    private DateTimeOffset fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(string? keyId, CancellationToken ct = default)
    {
        if (IsUsable(keyId))
        {
            return keys;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (IsUsable(keyId) || Clock.GetUtcNow() - lastAttempt < MinFetchInterval)
            {
                return keys;
            }

            lastAttempt = Clock.GetUtcNow();
            if (await FetchAsync(ct) is { } fetched)
            {
                keys = fetched;
                fetchedAt = lastAttempt;
            }

            return keys;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        gate.Dispose();
    }

    private bool IsUsable(string? keyId) =>
        Clock.GetUtcNow() - fetchedAt < MaxAge && keys.Any(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));

    private async Task<IReadOnlyList<SecurityKey>?> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            var json = await client.GetStringAsync(settings.KeysUrl, ct);
            var fetched = new JsonWebKeySet(json).Keys
                .Where(k => k.Kty == JsonWebAlgorithmsKeyTypes.RSA && !string.IsNullOrEmpty(k.Kid))
                .Cast<SecurityKey>()
                .ToList();
            logger.LogInformation("Fetched {Count} Sign in with Apple signing keys", fetched.Count);
            return fetched;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Fetching the Sign in with Apple signing keys failed; keeping {Count} cached", keys.Count);
            return null;
        }
    }
}
