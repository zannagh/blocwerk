// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>What <c>/device-link</c> accepts before it may ever redirect, and the PKCE and callback helpers.</summary>
public class DeviceLinkRequestTests
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string State = "abcdefghijklmnop_-0123";

    [Fact]
    public void Pkce_MatchesTheRfc7636Vector()
    {
        Assert.Equal(Challenge, DeviceLinkPkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.True(DeviceLinkPkce.Verifies("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", Challenge));
        Assert.False(DeviceLinkPkce.Verifies("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXX", Challenge));
        Assert.False(DeviceLinkPkce.Verifies("short", Challenge));
        Assert.False(DeviceLinkPkce.Verifies(null, Challenge));
    }

    [Theory]
    [InlineData(Challenge, "1MM-SP7")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "1W0-771")]
    public void MatchCode_IsSixCrockfordCharactersOfTheChallengeHash(string challenge, string expected)
    {
        Assert.Equal(expected, DeviceLinkPkce.MatchCode(challenge));
    }

    [Fact]
    public void ValidRequest_Parses()
    {
        var request = Parse(redirect: "blocwerk-companion://auth");

        Assert.NotNull(request);
        Assert.Equal("Patrick's iPhone", request!.DeviceName);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("blocwerk-companion://auth/")]
    [InlineData("blocwerk-companion://auth?x=1")]
    [InlineData("BLOCWERK-COMPANION://auth")]
    [InlineData("blocwerk-companion://evil")]
    [InlineData("other-app://auth")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyOtherRedirect_IsRefused(string? redirect)
    {
        Assert.Null(Parse(redirect: redirect));
    }

    [Theory]
    [InlineData("plain", Challenge, State)]
    [InlineData("S256", "tooShort", State)]
    [InlineData("S256", Challenge + "x", State)]
    [InlineData("S256", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM", State)]
    [InlineData("S256", Challenge, "short")]
    [InlineData("S256", Challenge, "has spaces in it, sixteen+")]
    public void MalformedPkceOrState_IsRefused(string method, string challenge, string state)
    {
        var request = DeviceLinkRequest.TryParse(
            state, challenge, method, "Phone", DeviceLinkRequest.RedirectUri, out var reason);

        Assert.Null(request);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("  My\tPhone \n ", "My Phone")]
    [InlineData("Bob​s\u0000Phone", "BobsPhone")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void DeviceName_IsCleaned(string? raw, string? expected)
    {
        Assert.Equal(expected, DeviceLinkRequest.CleanDeviceName(raw));
    }

    [Fact]
    public void DeviceName_IsCutTo64()
    {
        Assert.Equal(64, DeviceLinkRequest.CleanDeviceName(new string('a', 200))!.Length);
    }

    [Fact]
    public void Callback_AlwaysGoesToTheAppScheme_WithEscapedValues()
    {
        Assert.Equal("blocwerk-companion://auth?code=c0de&state=a%26b%3Dc", DeviceLinkCallback.WithCode("c0de", "a&b=c"));
        Assert.Equal("blocwerk-companion://auth?error=access_denied&state=s", DeviceLinkCallback.Denied("s"));
    }

    private static DeviceLinkRequest? Parse(string? redirect) =>
        DeviceLinkRequest.TryParse(State, Challenge, "S256", "Patrick's iPhone", redirect, out _);
}
