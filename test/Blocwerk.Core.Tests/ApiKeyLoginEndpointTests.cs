using System.Net;
using Blocwerk.Authentication.Endpoints;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pins the personal-API-key browser login: off (404) unless configured, a session only for a
/// personal key of a live, real user, the key taken from the Authorization header alone, no open
/// redirect, and a per-IP rate limit. Runs in the Production environment on purpose.
/// </summary>
public sealed class ApiKeyLoginEndpointTests
{
    [Fact]
    public async Task Disabled_AnswersNotFound_EvenForAValidKey()
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: false);
        var user = await host.AddUserAsync("Climber__gh|1");
        var token = await host.AddKeyAsync(user.Id);

        var response = await host.LoginAsync(token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ValidPersonalKey_IssuesTheCookieSessionOfItsOwner()
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true);
        var user = await host.AddUserAsync("Climber__gh|1");
        await host.AddUserAsync("Someone Else__gh|2");
        var token = await host.AddKeyAsync(user.Id);

        var response = await host.LoginAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        Assert.Equal(user.Id.ToString(), await host.WhoAmIAsync(response));

        // Marked as a key session, naming the key it was signed in with.
        var marker = (await host.WhoAmIAsync(response, ApiKeyLoginTestHost.ClaimsPath)).Split('|');
        Assert.Equal("apikey", marker[0]);
        Assert.True(Guid.TryParse(marker[1], out _));
    }

    [Fact]
    public async Task UserOffTheAllowList_IsRefused()
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true);
        var user = await host.AddUserAsync("Climber__gh|1", allow: false);
        var token = await host.AddKeyAsync(user.Id);

        var response = await host.LoginAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("anonymous", await host.WhoAmIAsync(response));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("installation")]
    [InlineData("ghost")]
    [InlineData("deleted-user")]
    [InlineData("unknown")]
    public async Task IneligibleKey_IsRefusedWithAGeneric401(string kind)
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true);
        var token = await CreateIneligibleKeyAsync(host, kind);

        var response = await host.LoginAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid API key.", await response.Content.ReadAsStringAsync());
        Assert.Equal("anonymous", await host.WhoAmIAsync(response));
    }

    [Fact]
    public async Task KeyInTheQueryString_IsIgnored()
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true);
        var user = await host.AddUserAsync("Climber__gh|1");
        var token = await host.AddKeyAsync(user.Id);

        var response = await host.LoginAsync(
            token: null,
            query: $"?apiKey={token}&key={token}&access_token={token}&token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("anonymous", await host.WhoAmIAsync(response));
    }

    [Fact]
    public async Task Redirect_FollowsALocalReturnUrl()
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true);
        var user = await host.AddUserAsync("Climber__gh|1");
        var token = await host.AddKeyAsync(user.Id);

        var response = await host.LoginAsync(token, "?redirect=1&returnUrl=%2Fwalls%2Fabc%3Fx%3D1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/walls/abc?x=1", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Redirect_NeverFollowsAnOffSiteReturnUrl(string returnUrl)
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true);
        var user = await host.AddUserAsync("Climber__gh|1");
        var token = await host.AddKeyAsync(user.Id);

        var response = await host.LoginAsync(token, $"?redirect=1&returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/walls", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task KioskDevice_IsRefused()
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true, isKiosk: true);
        var user = await host.AddUserAsync("Climber__gh|1");
        var token = await host.AddKeyAsync(user.Id);

        var response = await host.LoginAsync(token);

        // The kiosk allow-list refuses the path before the endpoint runs; the validator would
        // refuse a kiosk context again if it ever did.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task RateLimit_RefusesTheAttemptAfterTheWindowIsSpent()
    {
        await using var host = await ApiKeyLoginTestHost.StartAsync(enabled: true);
        var user = await host.AddUserAsync("Climber__gh|1");
        var token = await host.AddKeyAsync(user.Id);

        for (var i = 0; i < ApiKeyLoginEndpoints.PermitsPerWindow; i++)
        {
            var guess = await host.LoginAsync("bwk_" + new string('0', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, guess.StatusCode);
        }

        // Even the right key is turned away once the window is spent.
        var response = await host.LoginAsync(token);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("anonymous", await host.WhoAmIAsync(response));
    }

    private static async Task<string> CreateIneligibleKeyAsync(ApiKeyLoginTestHost host, string kind)
    {
        var user = await host.AddUserAsync("Climber__gh|1");
        return kind switch
        {
            "revoked" => await host.AddKeyAsync(user.Id, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            "expired" => await host.AddKeyAsync(user.Id, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            "installation" => await host.AddKeyAsync(user.Id, ApiKeyScope.Installation),
            "ghost" => await host.AddKeyAsync(GhostUser.Id),
            "deleted-user" => await host.AddKeyAsync(
                (await host.AddUserAsync(PlaceholderIdentity.DeletedIdentifier(Guid.NewGuid()), DateTimeOffset.UtcNow)).Id),
            _ => "bwk_" + new string('a', 64),
        };
    }
}
