// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Controllers;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
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
/// <c>Clear-Site-Data: "cache"</c> where an identity leaves a device for good (design §6.6, rule 2): an account sign-out
/// (and a kiosk unregistration, in <c>KioskEndpointTests</c>), on a 200 page because WebKit ignores it on a redirect.
/// Nowhere else, and never "storage".
/// </summary>
public class SiteDataClearingTests
{
    /// <summary>
    /// The cookie handler adds nothing: logins, kiosk act-as and the validators' forced sign-outs carry no header (the
    /// partition check of the next page covers a forced sign-out).
    /// </summary>
    [Fact]
    public async Task CookieSignInsAndSignOuts_CarryNoHeader()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var signIn = await client.GetAsync("/in");
        var signOut = await client.GetAsync("/out");

        Assert.False(signIn.Headers.Contains(SiteDataClearing.HeaderName));
        Assert.False(signOut.Headers.Contains(SiteDataClearing.HeaderName));
    }

    /// <summary>WebKit ignores Clear-Site-Data on a 3xx: the account sign-out is a 200 page that carries it and moves on.</summary>
    [Fact]
    public async Task AnAccountLogout_IsA200Page_CarryingTheHeader_ThatMovesOn()
    {
        var (controller, http, authentication) = Controller(isKiosk: false);

        var page = Assert.IsType<ContentResult>(await controller.Logout());

        await authentication.Received(1).SignOutAsync(http, CookieAuthenticationDefaults.AuthenticationScheme, Arg.Any<AuthenticationProperties>());
        Assert.Equal(StatusCodes.Status200OK, page.StatusCode);
        Assert.Equal("\"cache\"", http.Response.Headers[SiteDataClearing.HeaderName].ToString());
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.StartsWith("text/html", page.ContentType);
        Assert.Contains("<meta http-equiv=\"refresh\" content=\"1;url=/\">", page.Content);
        Assert.Contains("<a href=\"/\">Continue</a>", page.Content);
        Assert.Contains("location.replace(\"/\")", page.Content);
        Assert.Contains("new Event('bw:identity-changed')", page.Content);
        Assert.Contains("window.bwDeployDrafts.clearAll()", page.Content);
        Assert.True(page.Content!.IndexOf("<script src=\"/js/deploy-drafts.js\"></script>", StringComparison.Ordinal)
            < page.Content.IndexOf("new Event('bw:identity-changed')", StringComparison.Ordinal));
        Assert.True(page.Content.IndexOf("<script src=\"/js/asset-wipe.js\"></script>", StringComparison.Ordinal)
            < page.Content.IndexOf("window.bwAssetWipe.signedOut()", StringComparison.Ordinal));
        Assert.True(page.Content.IndexOf("<script src=\"/js/asset-ledger.js\"></script>", StringComparison.Ordinal)
            < page.Content.IndexOf("<script src=\"/js/asset-wipe.js\"></script>", StringComparison.Ordinal));
    }

    /// <summary>A kiosk session ending (release, idle) keeps the tablet's shared, wall-pinned cache.</summary>
    [Fact]
    public async Task AKioskSessionLogout_StaysARedirect_WithoutTheHeader()
    {
        var (controller, http, _) = Controller(isKiosk: true);

        Assert.IsType<LocalRedirectResult>(await controller.Logout());
        Assert.False(http.Response.Headers.ContainsKey(SiteDataClearing.HeaderName));
    }

    [Theory]
    [InlineData("//evil.example/")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")]
    public void TheSignedOutPage_OnlyEverMovesOnToALocalPath(string next)
    {
        var page = SiteDataClearing.SignedOutPage(new DefaultHttpContext().Response, next);

        Assert.DoesNotContain("evil", page.Content);
        Assert.Contains("location.replace(\"/\")", page.Content);
    }

    [Fact]
    public void TheHeader_IsCacheOnly_NeverStorage()
    {
        var http = new DefaultHttpContext();

        SiteDataClearing.Apply(http.Response);

        Assert.Equal("\"cache\"", http.Response.Headers[SiteDataClearing.HeaderName].ToString());
        Assert.DoesNotContain("storage", SiteDataClearing.CacheOnly, StringComparison.Ordinal);
    }

    private static (AccountController Controller, DefaultHttpContext Http, IAuthenticationService Authentication) Controller(bool isKiosk)
    {
        var authentication = Substitute.For<IAuthenticationService>();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider(),
        };
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(isKiosk);
        var controller = new AccountController(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, kiosk, null!, new EphemeralDataProtectionProvider())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (controller, http, authentication);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie();
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
