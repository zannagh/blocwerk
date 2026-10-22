// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Services;
using Blocwerk.Contracts.Assets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The sign-out reason through the real cookie handler (Phase 3 PR 3.5): an expired ticket and a missing cookie read
/// <c>expired</c>; a validator rejection reads <c>forced</c> on that request and, through the sticky marker, on the
/// device's later cookie-less requests, until a sign-in deletes it; and the app's 401 under <c>/api</c> (the cookie
/// challenge, <c>Program.ConfigureApiCookieBehaviour</c>) carries the reason, the feed's in its body too.
/// </summary>
public sealed class SignOutReasonPipelineTests : IAsyncDisposable
{
    private const string Reject = "X-Test-Reject";

    private WebApplication? app;

    [Fact]
    public async Task NoCookie_AndAnExpiredTicket_ReadExpired()
    {
        var client = await StartAsync();
        Assert.Equal(SignOutReasons.Expired, await ReasonAsync(client, cookies: null));

        var expired = await SignInAsync(client, TimeSpan.FromMinutes(-5));
        var (signedIn, reason) = await WhoAsync(client, expired);
        Assert.False(signedIn);
        Assert.Equal(SignOutReasons.Expired, reason);
    }

    [Fact]
    public async Task AValidatorRejection_IsForced_AndStaysForcedUntilTheNextSignIn()
    {
        var client = await StartAsync();
        var session = await SignInAsync(client, TimeSpan.FromHours(1));
        Assert.True((await WhoAsync(client, session)).SignedIn);

        using var rejected = await SendAsync(client, "/who", session, reject: true);
        Assert.Equal(SignOutReasons.Forced, await rejected.Content.ReadAsStringAsync());
        var marker = CookiesOf(rejected).Single(c => c.StartsWith(SignOutReason.MarkerCookie + "=", StringComparison.Ordinal));
        Assert.StartsWith($"{SignOutReason.MarkerCookie}=forced", marker);

        // The session cookie is gone; the device's next request carries only the marker.
        var later = $"{SignOutReason.MarkerCookie}=forced";
        Assert.Equal(SignOutReasons.Forced, await ReasonAsync(client, later));

        // Signing in again deletes it, so a later expiry locks again.
        using var signIn = await SendAsync(client, "/signin?minutes=60", later);
        Assert.Contains(CookiesOf(signIn), c => c.StartsWith($"{SignOutReason.MarkerCookie}=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheApiChallenge_Is401_WithTheReasonHeader_AndTheFeedsBodyCarriesIt()
    {
        var client = await StartAsync();

        using var manifest = await SendAsync(client, $"/api/walls/{Guid.NewGuid()}/manifest", $"{SignOutReason.MarkerCookie}=gone");
        Assert.Equal(HttpStatusCode.Unauthorized, manifest.StatusCode);
        Assert.Equal(SignOutReasons.Gone, manifest.Headers.GetValues(SignOutReasons.HeaderName).Single());

        using var feed = await SendAsync(client, $"/api/walls/{Guid.NewGuid()}/changes", cookies: null);
        Assert.Equal(HttpStatusCode.Unauthorized, feed.StatusCode);
        Assert.Equal(SignOutReasons.Expired, feed.Headers.GetValues(SignOutReasons.HeaderName).Single());
        using var body = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.Equal("unauthenticated", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(SignOutReasons.Expired, body.RootElement.GetProperty("reason").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Events.OnValidatePrincipal = context =>
                context.Request.Headers.ContainsKey(Reject) ? SignOutReason.RejectAsync(context) : Task.CompletedTask;
            SignOutReason.Configure(options);
        });
        builder.Services.AddAuthorization();
        Blocwerk.Web.Program.ConfigureApiCookieBehaviour(builder.Services);

        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/signin", async (HttpContext http, int minutes) =>
        {
            var now = DateTimeOffset.UtcNow;
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], CookieAuthenticationDefaults.AuthenticationScheme));
            await http.SignInAsync(principal, new AuthenticationProperties { IssuedUtc = now.AddHours(-2), ExpiresUtc = now.AddMinutes(minutes), AllowRefresh = false });
            return "ok";
        });
        app.MapGet("/who", (HttpContext http) => http.User.Identity?.IsAuthenticated == true ? "signed-in" : SignOutReason.Of(http));
        app.MapGet("/api/walls/{wallId:guid}/manifest", () => "never").RequireAuthorization();
        app.MapGet("/api/walls/{wallId:guid}/changes", () => "never").RequireAuthorization();
        await app.StartAsync();
        return app.GetTestClient();
    }

    private static async Task<string> SignInAsync(HttpClient client, TimeSpan lifetime)
    {
        using var response = await SendAsync(client, $"/signin?minutes={(int)lifetime.TotalMinutes}", cookies: null);
        response.EnsureSuccessStatusCode();
        return CookiesOf(response).Single(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)).Split(';')[0];
    }

    private static async Task<(bool SignedIn, string Reason)> WhoAsync(HttpClient client, string? cookies)
    {
        using var response = await SendAsync(client, "/who", cookies);
        var text = await response.Content.ReadAsStringAsync();
        return (text == "signed-in", text);
    }

    private static async Task<string> ReasonAsync(HttpClient client, string? cookies) => (await WhoAsync(client, cookies)).Reason;

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string? cookies, bool reject = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookies is not null)
        {
            request.Headers.Add("Cookie", cookies);
        }

        if (reject)
        {
            request.Headers.Add(Reject, "1");
        }

        return client.SendAsync(request);
    }

    private static List<string> CookiesOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
}
