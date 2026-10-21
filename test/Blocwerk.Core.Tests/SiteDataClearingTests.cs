// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Controllers;
using Blocwerk.Authentication.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>Clear-Site-Data: "cache"</c> on every identity switch, so the private immutable asset responses of one identity
/// never outlive it in a shared device's HTTP cache: logout, and every sign-in and sign-out of the cookie handler
/// (logins, kiosk act-as, the validators' forced sign-outs). Never on an ordinary request, and never "storage".
/// </summary>
public class SiteDataClearingTests
{
    [Fact]
    public async Task EveryCookieSignInAndSignOut_ClearsTheHttpCache_AndAPlainRequestDoesNot()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var signIn = await client.GetAsync("/in");
        var cookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        using var plain = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        plain.Headers.Add("Cookie", cookie);
        var whoami = await client.SendAsync(plain);
        var signOut = await client.GetAsync("/out");

        Assert.Equal(["\"cache\""], signIn.Headers.GetValues(SiteDataClearing.HeaderName));
        Assert.Equal("u1", await whoami.Content.ReadAsStringAsync());
        Assert.False(whoami.Headers.Contains(SiteDataClearing.HeaderName));
        Assert.Equal(["\"cache\""], signOut.Headers.GetValues(SiteDataClearing.HeaderName));
    }

    [Fact]
    public async Task Logout_ClearsTheHttpCache_EvenWithoutTheCookieEvents()
    {
        var authentication = Substitute.For<IAuthenticationService>();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider(),
        };
        var controller = new AccountController(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new EphemeralDataProtectionProvider())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        Assert.IsType<LocalRedirectResult>(await controller.Logout());

        await authentication.Received(1).SignOutAsync(http, CookieAuthenticationDefaults.AuthenticationScheme, Arg.Any<AuthenticationProperties>());
        Assert.Equal("\"cache\"", http.Response.Headers[SiteDataClearing.HeaderName].ToString());
    }

    [Fact]
    public void TheHeader_IsCacheOnly_NeverStorage()
    {
        var http = new DefaultHttpContext();

        SiteDataClearing.Apply(http.Response);

        Assert.Equal("\"cache\"", http.Response.Headers[SiteDataClearing.HeaderName].ToString());
        Assert.DoesNotContain("storage", SiteDataClearing.CacheOnly, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => SiteDataClearing.Configure(options));
        var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/in", (HttpContext h) => h.SignInAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", "u1")], CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapGet("/out", (HttpContext h) => h.SignOutAsync());
        app.MapGet("/whoami", (HttpContext h) => h.User.FindFirst("uid")?.Value ?? "anonymous");
        await app.StartAsync();
        return app;
    }
}
