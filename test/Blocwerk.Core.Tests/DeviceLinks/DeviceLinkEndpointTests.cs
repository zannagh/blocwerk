// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Web.DeviceLinks;
using Blocwerk.Web.Endpoints;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>
/// The device link end to end over HTTP: consent post → redirect to the app → code exchange → the key reads the
/// account and revokes itself. Plus every refusal: cancel, a foreign redirect, no antiforgery token, signed out, a
/// reused code and the rate limit.
/// </summary>
public sealed class DeviceLinkEndpointTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string State = "state-0123456789abcdef";
    private const string Device = "Test iPhone";

    [Fact]
    public async Task Link_RedirectsToTheApp_AndTheCodeBuysAKeyThatReadsAndRevokesItself()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();

        var consent = await host.PostConsentAsync(Consent("link"));

        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var location = consent.Headers.Location!.OriginalString;
        Assert.StartsWith("blocwerk-companion://auth?code=", location);
        Assert.EndsWith($"&state={State}", location);
        var code = location.Split("code=")[1].Split('&')[0];

        var exchange = await ExchangeAsync(host, code, Verifier);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.Equal("no-store", exchange.Headers.CacheControl!.ToString());
        var body = await exchange.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal("iPhone: Test iPhone", body.GetProperty("name").GetString());

        var account = await host.WithKeyAsync(HttpMethod.Get, "/api/v1/me/account", token);
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        var me = await account.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(host.Owner.Id, me.GetProperty("id").GetGuid());
        Assert.Equal("Climber", me.GetProperty("displayName").GetString());
        Assert.Equal("iPhone: Test iPhone", me.GetProperty("keyName").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("avatarUrl").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await host.WithKeyAsync(HttpMethod.Get, "/api/v1/me/avatar", token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await host.WithKeyAsync(HttpMethod.Delete, "/api/v1/me/api-key", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.WithKeyAsync(HttpMethod.Get, "/api/v1/me/account", token)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("forged")]
    public async Task WithoutAFreshPageTicket_ThePageIsShownAgain_AndNoCodeIsMade(string ticket)
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();

        var consent = await host.PostConsentAsync(Consent("link"), ticket: ticket);

        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var location = consent.Headers.Location!.OriginalString;
        Assert.StartsWith("/device-link?", location);
        Assert.Contains("reopened=1", location);
        Assert.Contains($"state={State}", location);
    }

    [Fact]
    public async Task ConsentPosts_AreLimitedPerUser()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();
        for (var i = 0; i < DeviceLinkLimits.DefaultConfirmsPerUserPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await host.PostConsentAsync(Consent("cancel"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.PostConsentAsync(Consent("cancel"))).StatusCode);
    }

    [Fact]
    public async Task LiveCodes_AreCappedPerUser()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();
        for (var i = 0; i < DeviceLinkPolicy.MaxLiveCodes; i++)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await host.PostConsentAsync(Consent("link"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.PostConsentAsync(Consent("link"))).StatusCode);
    }

    [Fact]
    public async Task Exchanges_AlsoShareAServerWideLimit()
    {
        await using var host = await DeviceLinkTestHost.StartAsync(globalExchangesPerMinute: 3);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(host, "x", Verifier)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await ExchangeAsync(host, "x", Verifier)).StatusCode);
    }

    [Fact]
    public async Task Cancel_TellsTheAppNo()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();

        var consent = await host.PostConsentAsync(Consent("cancel"));

        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        Assert.Equal($"blocwerk-companion://auth?error=access_denied&state={State}", consent.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("blocwerk-companion://evil")]
    [InlineData("//evil.example")]
    public async Task ForeignRedirect_IsRefusedWithoutARedirect(string redirect)
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();

        var consent = await host.PostConsentAsync(Consent("link", redirect));

        Assert.Equal(HttpStatusCode.BadRequest, consent.StatusCode);
        Assert.Null(consent.Headers.Location);
    }

    [Fact]
    public async Task PostWithoutAntiforgeryToken_IsRefused()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();

        var consent = await host.PostConsentAsync(Consent("link"), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, consent.StatusCode);
        Assert.Null(consent.Headers.Location);
    }

    [Fact]
    public async Task SignedOut_IsSentToSignIn_NeverToTheApp()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();

        var consent = await host.PostConsentAsync(Consent("link"));

        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        Assert.DoesNotContain("blocwerk-companion", consent.Headers.Location!.OriginalString);
        Assert.Contains("login", consent.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReusedCodeOrWrongVerifier_AnswersInvalidGrant()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        await host.SignInAsync();
        var code = await LinkAsync(host);

        var wrong = await ExchangeAsync(host, code, Verifier[..^1] + "X");
        var reused = await ExchangeAsync(host, code, Verifier);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("invalid_grant", await wrong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
    }

    [Fact]
    public async Task Exchange_TakesJsonOnly_SoACrossSiteFormCannotPostIt()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "x", ["code_verifier"] = Verifier });
        var response = await host.Client.PostAsync("/api/device-link/token", form);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_IsRateLimited()
    {
        await using var host = await DeviceLinkTestHost.StartAsync();
        for (var i = 0; i < DeviceLinkRateLimit.PermitsPerWindow; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(host, "x", Verifier)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await ExchangeAsync(host, "x", Verifier)).StatusCode);
    }

    private static async Task<string> LinkAsync(DeviceLinkTestHost host)
    {
        var consent = await host.PostConsentAsync(Consent("link"));
        return consent.Headers.Location!.OriginalString.Split("code=")[1].Split('&')[0];
    }

    private static Task<HttpResponseMessage> ExchangeAsync(DeviceLinkTestHost host, string code, string verifier) =>
        host.Client.PostAsJsonAsync("/api/device-link/token", new { code, code_verifier = verifier, device = Device });

    private static Dictionary<string, string> Consent(string decision, string redirect = DeviceLinkRequest.RedirectUri) => new()
    {
        ["state"] = State,
        ["code_challenge"] = DeviceLinkPkce.Challenge(Verifier),
        ["code_challenge_method"] = "S256",
        ["device"] = Device,
        ["redirect_uri"] = redirect,
        ["decision"] = decision,
    };
}
