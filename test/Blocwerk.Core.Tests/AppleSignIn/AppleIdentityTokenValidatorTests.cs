// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.AppleSignIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>
/// The validator against a stubbed Apple JWKS: a good token yields its subject; a foreign signature, the wrong audience
/// or issuer, an expired or future token and a nonce mismatch all yield null; an unknown key id refetches the keys.
/// </summary>
public sealed class AppleIdentityTokenValidatorTests
{
    private const string Nonce = "server-issued-nonce-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly AppleTestKey apple = new("apple-1");
    private readonly MutableTestClock clock = new(Now);

    [Fact]
    public async Task AGoodToken_YieldsItsSubject()
    {
        var (validator, _) = Validator(apple);

        var identity = await validator.ValidateAsync(apple.Mint("001234.abc", Nonce, Now), Nonce);

        Assert.Equal("001234.abc", identity?.Subject);
    }

    [Fact]
    public async Task AWebServicesIdToken_IsAccepted_ByTheDefaultAudiences()
    {
        var (validator, _) = Validator(apple);

        var token = apple.Mint("001234.abc", Nonce, Now, audience: AppleSignInSettings.WebServicesId);

        Assert.NotNull(await validator.ValidateAsync(token, Nonce));
    }

    [Fact]
    public async Task ATokenSignedByAnotherKeyWithTheSameId_IsRefused()
    {
        var (validator, _) = Validator(apple);
        var forger = new AppleTestKey("apple-1");

        Assert.Null(await validator.ValidateAsync(forger.Mint("001234.abc", Nonce, Now), Nonce));
    }

    [Theory]
    [InlineData("com.example.other", AppleIdentityTokenValidator.Issuer)]
    [InlineData(null, AppleIdentityTokenValidator.Issuer)]
    [InlineData(AppleSignInSettings.IosBundleId, "https://evil.example")]
    public async Task TheWrongAudienceOrIssuer_IsRefused(string? audience, string issuer)
    {
        var (validator, _) = Validator(apple);

        Assert.Null(await validator.ValidateAsync(apple.Mint("s", Nonce, Now, audience, issuer), Nonce));
    }

    [Fact]
    public async Task ConfiguredAudiences_ReplaceTheDefault()
    {
        var (validator, _) = Validator(apple, new AppleSignInSettings { AllowedAudiences = ["com.example.other"] });

        Assert.Null(await validator.ValidateAsync(apple.Mint("s", Nonce, Now), Nonce));
        Assert.NotNull(await validator.ValidateAsync(apple.Mint("s", Nonce, Now, "com.example.other"), Nonce));
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused_ButASmallSkewIsForgiven()
    {
        var (validator, _) = Validator(apple);
        var token = apple.Mint("s", Nonce, Now, lifetime: TimeSpan.FromMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(10.5));
        Assert.NotNull(await validator.ValidateAsync(token, Nonce));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await validator.ValidateAsync(token, Nonce));
    }

    [Fact]
    public async Task ATokenIssuedInTheFuture_IsRefused()
    {
        var (validator, _) = Validator(apple);

        Assert.Null(await validator.ValidateAsync(apple.Mint("s", Nonce, Now.AddMinutes(5)), Nonce));
    }

    [Theory]
    [InlineData("another-nonce-0123456789abcdef")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ANonceThatDoesNotHashToTheClaim_IsRefused(string? nonce)
    {
        var (validator, _) = Validator(apple);

        Assert.Null(await validator.ValidateAsync(apple.Mint("s", Nonce, Now), nonce));
    }

    [Fact]
    public async Task AClaimHoldingTheRawNonce_IsRefused()
    {
        var (validator, _) = Validator(apple);

        Assert.Null(await validator.ValidateAsync(apple.Mint("s", Nonce, Now, rawNonceClaim: Nonce), Nonce));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public async Task Garbage_IsRefused(string token)
    {
        var (validator, _) = Validator(apple);

        Assert.Null(await validator.ValidateAsync(token, Nonce));
    }

    [Fact]
    public async Task AnUnknownKeyId_RefetchesTheKeys_SoARotatedKeyVerifies()
    {
        var (validator, jwks) = Validator(apple);
        Assert.NotNull(await validator.ValidateAsync(apple.Mint("s", Nonce, Now), Nonce));
        Assert.Equal(1, jwks.Fetches);

        var rotated = new AppleTestKey("apple-2");
        jwks.Json = AppleTestKey.Jwks(apple, rotated);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.NotNull(await validator.ValidateAsync(rotated.Mint("s", Nonce, clock.GetUtcNow()), Nonce));
        Assert.Equal(2, jwks.Fetches);
    }

    private (AppleIdentityTokenValidator Validator, StubJwksHandler Jwks) Validator(
        AppleTestKey key,
        AppleSignInSettings? settings = null)
    {
        settings ??= new AppleSignInSettings();
        var jwks = new StubJwksHandler(AppleTestKey.Jwks(key));
        var cache = new AppleKeyCache(new StubHttpClientFactory(jwks), settings, NullLogger<AppleKeyCache>.Instance, clock);
        return (new AppleIdentityTokenValidator(cache, settings, NullLogger<AppleIdentityTokenValidator>.Instance, clock), jwks);
    }
}
