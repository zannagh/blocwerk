// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Apple;
using Blocwerk.Authentication.Controllers;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>
/// Apple's cross-site form POST to <c>/oauth-callback/apple</c>: accepted only for a state this server issued for Apple
/// AND sealed in this browser's cookie, then a 303 into the usual <c>/signing-in</c> GET chain.
/// </summary>
public sealed class AppleCallbackControllerTests : IDisposable
{
    private readonly AppleWebHarness harness = new();
    private readonly RedirectUriProvider redirects = new();

    [Fact]
    public void AKnownStateWithItsCookie_ParksTheCode_And303sIntoSigningIn()
    {
        var cookie = Begin("state-1");

        var (result, http) = Post("state-1", "code-1", cookie, user: """{"name":{"firstName":"Ada"}}""");

        Assert.Equal(StatusCodes.Status303SeeOther, result.StatusCode);
        Assert.Equal("/signing-in?state=state-1&code=code-1", http.Response.Headers.Location.ToString());
        Assert.True(harness.Web.TryTakeTicket("code-1", out var ticket));
        Assert.Equal("Ada", ticket.Name);
        Assert.DoesNotContain("__Host-bw_apple_oauth=;", http.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void TheParkedCode_CompletesOnlyInTheBrowserThatStartedIt_ForItsOwnState()
    {
        var cookie = Begin("state-1");
        Post("state-1", "code-1", cookie);
        Assert.True(harness.Web.TryTakeTicket("code-1", out var ticket));

        Assert.False(Bound(ticket, "state-1", cookie: null), "a /signing-in URL relayed into another browser");
        Assert.False(Bound(ticket, "state-1", Begin("state-2")), "another browser mid-way through its own round trip");
        Assert.False(Bound(ticket, "state-other", cookie), "the right browser, another state in the URL");
        Assert.True(Bound(ticket, "state-1", cookie));
    }

    [Fact]
    public void ACallbackFromAnotherBrowser_WithoutTheCookie_IsRefused()
    {
        Begin("state-1");

        var (result, http) = Post("state-1", "code-1", cookie: null);

        Assert.Equal(StatusCodes.Status303SeeOther, result.StatusCode);
        Assert.Equal("/account/login?error=auth_failed", http.Response.Headers.Location.ToString());
        Assert.False(harness.Web.TryTakeTicket("code-1", out _));
    }

    [Fact]
    public void AnUnknownState_OrOneIssuedForAnotherProvider_IsRefused()
    {
        var cookie = Begin("state-1");
        redirects.AddRedirectUri("state-gh", new RedirectSettings { Uri = "x", Provider = "github" });

        var (_, unknown) = Post("state-x", "code-1", cookie);
        var (_, github) = Post("state-gh", "code-2", cookie);

        Assert.Equal("/account/login?error=auth_failed", unknown.Response.Headers.Location.ToString());
        Assert.Equal("/account/login?error=auth_failed", github.Response.Headers.Location.ToString());
        Assert.False(harness.Web.TryTakeTicket("code-1", out _));
        Assert.False(harness.Web.TryTakeTicket("code-2", out _));
    }

    [Fact]
    public void AStateIsSpentByItsFirstCallback()
    {
        var cookie = Begin("state-1");
        Post("state-1", "code-1", cookie);

        var (_, replay) = Post("state-1", "code-2", cookie);

        Assert.Equal("/account/login?error=auth_failed", replay.Response.Headers.Location.ToString());
    }

    [Fact]
    public void ACancelAtApple_GoesBackToTheLoginPage()
    {
        var cookie = Begin("state-1");

        var (_, http) = Post("state-1", code: null, cookie, error: "user_cancelled_authorize");

        Assert.Equal("/account/login?error=cancelled", http.Response.Headers.Location.ToString());
    }

    public void Dispose() => harness.Dispose();

    private bool Bound(AppleCallbackTicket ticket, string state, string? cookie)
    {
        var http = new DefaultHttpContext();
        if (cookie is not null)
        {
            http.Request.Headers.Cookie = cookie;
        }

        return harness.Web.IsBoundToThisBrowser(ticket, state, http.Request, http.Response);
    }

    // The authorize leg as /account/external runs it: the state for Apple, and the browser's binding cookie.
    private string Begin(string state)
    {
        redirects.AddRedirectUri(state, new RedirectSettings { Uri = "https://blocwerk.test/account/callback", Provider = "apple" });
        var http = new DefaultHttpContext();
        harness.Web.BeginAuthorize(http.Response, state);
        return AppleWebHarness.CookieHeader(http.Response);
    }

    private (StatusCodeResult Result, HttpContext Http) Post(
        string state,
        string? code,
        string? cookie,
        string? user = null,
        string? error = null)
    {
        var http = new DefaultHttpContext();
        if (cookie is not null)
        {
            http.Request.Headers.Cookie = cookie;
        }

        var controller = new AppleCallbackController(harness.Web, redirects)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (Assert.IsType<StatusCodeResult>(controller.Callback(state, code, user, error)), http);
    }
}
