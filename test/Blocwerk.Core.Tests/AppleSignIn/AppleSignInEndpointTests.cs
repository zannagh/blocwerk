// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>
/// The anonymous routes over HTTP: the token exchange's envelope on success, invalid_grant and the not-linked 404 on
/// refusal, the per-address limits, and the server-wide cap on nonce issuance.
/// </summary>
public sealed class AppleSignInEndpointTests
{
    [Fact]
    public async Task ALinkedAppleId_GetsTheTokenExchangesEnvelope()
    {
        await using var host = await AppleSignInTestHost.StartAsync();
        var user = await host.Bed.AddUserAsync("climber");
        await host.Bed.LinkAsync(user.Id, "apple-sub-1");

        var response = await host.SignInAsync(StubAppleValidator.Token("apple-sub-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith(Blocwerk.Core.Entities.ApiKey.TokenPrefix, body.GetProperty("token").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("keyId").GetGuid());
        Assert.Equal("iPhone: Test iPhone", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnUnlinkedAppleId_Answers404WithTheMessage()
    {
        await using var host = await AppleSignInTestHost.StartAsync();

        var response = await host.SignInAsync(StubAppleValidator.Token("apple-sub-1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AppleSignInController.NotLinkedMessage, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ABadToken_IsAnInvalidGrant()
    {
        await using var host = await AppleSignInTestHost.StartAsync();

        var response = await host.SignInAsync("forged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SignIns_AreLimitedPerAddress()
    {
        await using var host = await AppleSignInTestHost.StartAsync();
        var nonce = await host.NonceAsync("10.0.0.9");
        for (var i = 0; i < AppleSignInRateLimit.SignInsPerWindow; i++)
        {
            await host.SendAsync("/api/device-link/apple", new { identityToken = "x", nonce, device = "d" }, "10.0.0.9");
        }

        var limited = await host.SendAsync("/api/device-link/apple", new { identityToken = "x", nonce, device = "d" }, "10.0.0.9");
        var elsewhere = await host.SendAsync("/api/device-link/apple", new { identityToken = "x", nonce, device = "d" }, "10.0.0.10");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Nonces_AreLimitedPerAddress()
    {
        await using var host = await AppleSignInTestHost.StartAsync();
        for (var i = 0; i < AppleSignInRateLimit.NoncesPerWindow; i++)
        {
            await host.NonceAsync("10.0.0.7");
        }

        var limited = await host.SendAsync("/api/device-link/apple/nonce", null, "10.0.0.7");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task NonceIssuance_HasAServerWideCap()
    {
        await using var host = await AppleSignInTestHost.StartAsync(noncesPerMinute: 2);

        await host.NonceAsync("10.0.2.1");
        await host.NonceAsync("10.0.2.2");
        var capped = await host.SendAsync("/api/device-link/apple/nonce", null, "10.0.2.3");

        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
    }
}
