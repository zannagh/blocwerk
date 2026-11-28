// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.AppleSignIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>The JWKS cache: keeps the keys, refetches for an unknown key id, but at most once per interval.</summary>
public sealed class AppleKeyCacheTests
{
    private readonly MutableTestClock clock = new(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly AppleTestKey apple = new("apple-1");

    [Fact]
    public async Task AKnownKeyId_IsServedFromTheCache()
    {
        var (cache, jwks) = Cache();

        await cache.GetKeysAsync("apple-1");
        await cache.GetKeysAsync("apple-1");

        Assert.Equal(1, jwks.Fetches);
    }

    [Fact]
    public async Task UnknownKeyIds_RefetchAtMostOncePerInterval()
    {
        var (cache, jwks) = Cache();
        await cache.GetKeysAsync("apple-1");

        for (var i = 0; i < 20; i++)
        {
            await cache.GetKeysAsync("made-up-" + i);
        }

        Assert.Equal(1, jwks.Fetches);

        clock.Advance(AppleKeyCache.MinFetchInterval + TimeSpan.FromSeconds(1));
        var keys = await cache.GetKeysAsync("made-up");
        Assert.Equal(2, jwks.Fetches);
        Assert.Contains(keys, k => k.KeyId == "apple-1");
    }

    [Fact]
    public async Task OldKeys_AreFetchedAgain_AfterTheirMaxAge()
    {
        var (cache, jwks) = Cache();
        await cache.GetKeysAsync("apple-1");

        clock.Advance(AppleKeyCache.MaxAge);
        await cache.GetKeysAsync("apple-1");

        Assert.Equal(2, jwks.Fetches);
    }

    [Fact]
    public async Task AFailedFetch_KeepsTheCachedKeys()
    {
        var (cache, jwks) = Cache();
        await cache.GetKeysAsync("apple-1");

        jwks.Json = "not json";
        clock.Advance(AppleKeyCache.MaxAge);

        Assert.Contains(await cache.GetKeysAsync("apple-1"), k => k.KeyId == "apple-1");
    }

    private (AppleKeyCache Cache, StubJwksHandler Jwks) Cache()
    {
        var jwks = new StubJwksHandler(AppleTestKey.Jwks(apple));
        return (new AppleKeyCache(new StubHttpClientFactory(jwks), new AppleSignInSettings(), NullLogger<AppleKeyCache>.Instance, clock), jwks);
    }
}
