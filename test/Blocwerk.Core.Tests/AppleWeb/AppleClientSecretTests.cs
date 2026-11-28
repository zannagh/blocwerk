// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Apple;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>The client secret Apple's token endpoint takes: an ES256 JWT from the team for the Services ID.</summary>
public class AppleClientSecretTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheSecret_IsAnEs256Jwt_FromTheTeam_ForTheServicesId_SignedByTheKey()
    {
        var (key, pem) = AppleWebTestKeys.NewKey();
        var secret = new AppleClientSecret(AppleWebTestKeys.Settings(pem), new MutableTestClock(Start)).Get();

        Assert.NotNull(secret);
        var jwt = new JsonWebToken(secret);
        Assert.Equal("ES256", jwt.Alg);
        Assert.Equal(AppleWebTestKeys.KeyId, jwt.Kid);
        Assert.Equal(AppleWebTestKeys.TeamId, jwt.Issuer);
        Assert.Equal(AppleWebTestKeys.ServicesId, jwt.Subject);
        Assert.Equal(["https://appleid.apple.com"], jwt.Audiences);
        Assert.Equal(Start.UtcDateTime, jwt.IssuedAt);
        Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromDays(180), "Apple refuses a secret living over six months");

        var check = await new JsonWebTokenHandler().ValidateTokenAsync(secret, new TokenValidationParameters
        {
            IssuerSigningKey = new ECDsaSecurityKey(key),
            ValidIssuer = AppleWebTestKeys.TeamId,
            ValidAudience = "https://appleid.apple.com",
            ValidateLifetime = false,
        });
        Assert.True(check.IsValid, check.Exception?.Message);
    }

    [Fact]
    public void TheSecret_IsReused_ThenMadeAgainBeforeItExpires()
    {
        var clock = new MutableTestClock(Start);
        var secrets = new AppleClientSecret(AppleWebTestKeys.Settings(AppleWebTestKeys.NewKey().Pem), clock);

        var first = secrets.Get();
        clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(first, secrets.Get());

        clock.Advance(AppleClientSecret.Lifetime - TimeSpan.FromDays(10) - AppleClientSecret.RenewBefore + TimeSpan.FromMinutes(1));
        var renewed = secrets.Get();
        Assert.NotEqual(first, renewed);
        Assert.True(new JsonWebToken(renewed!).ValidTo > clock.GetUtcNow().UtcDateTime.AddDays(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----")]
    public void AMissingOrBrokenKey_LeavesTheProviderWithoutASecret(string pem)
    {
        var secrets = new AppleClientSecret(AppleWebTestKeys.Settings(pem));

        Assert.False(secrets.HasKey);
        Assert.Null(secrets.Get());
    }

    [Fact]
    public void TheKey_LoadsWithEscapedLineBreaks_OrAsItsBase64Body()
    {
        var pem = AppleWebTestKeys.NewKey().Pem;
        var escaped = pem.Replace("\n", "\\n", StringComparison.Ordinal);
        var body = string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));

        Assert.NotNull(AppleSigningKey.TryLoad(escaped));
        Assert.NotNull(AppleSigningKey.TryLoad(body));
    }

    [Fact]
    public void TheSettings_ReadExactlyTheFourVariables_AndAreOffWhileOneIsMissing()
    {
        var all = new Dictionary<string, string?>
        {
            ["APPLE_SIGNIN_PRIVATE_KEY"] = "pem",
            ["APPLE_SIGNIN_KEY_ID"] = "kid",
            ["APPLE_SIGNIN_TEAM_ID"] = "team",
            ["APPLE_SIGNIN_SERVICES_ID"] = "svc",
        };
        var settings = AppleWebSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(all).Build());
        Assert.True(settings.IsConfigured);
        Assert.Equal(("pem", "kid", "team", "svc"), (settings.PrivateKey, settings.KeyId, settings.TeamId, settings.ServicesId));

        foreach (var missing in all.Keys)
        {
            var partial = all.Where(p => p.Key != missing).ToDictionary();
            Assert.False(AppleWebSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(partial).Build()).IsConfigured);
        }
    }
}
