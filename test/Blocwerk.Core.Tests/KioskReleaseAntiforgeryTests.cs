// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Web;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The production 400 of 2026-10-23: the idle timer posted the release form with a token rendered for the acting
/// member, after the session had expired, and the antiforgery filter refused it — stranding the tablet on a bare
/// error page. Release now never needs the token (it only removes privilege); act-as still does; and any bodiless
/// error on a tablet's navigation becomes a page that goes back to the wall on its own.
/// </summary>
public class KioskReleaseAntiforgeryTests
{
    private const string AuthCookie = ".AspNetCore.Cookies";
    private const string TokenField = "__RequestVerificationToken";

    private static string Wall => $"/walls/{KioskReleaseTestHost.WallId}";

    [Fact]
    public async Task Release_WithATokenForAMemberWhoseSessionExpired_RedirectsToTheWall()
    {
        await using var host = await StartActingAsync();
        var token = await (await host.GetAsync("/form")).Content.ReadAsStringAsync();

        // The 30-minute window lapsed: the server rejects the cookie, the browser forgets it.
        host.Forget(AuthCookie);

        var response = await host.PostFormAsync("/kiosk/release", new Dictionary<string, string> { [TokenField] = token });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Wall, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Release_WithAStillLiveSession_AndNoToken_SignsOutAndRedirectsToTheWall()
    {
        await using var host = await StartActingAsync();

        var response = await host.PostFormAsync("/kiosk/release", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Wall, response.Headers.Location?.OriginalString);
        Assert.False(host.Has(AuthCookie));
        Assert.True(host.Has(KioskDeviceCookie.Name));
    }

    [Fact]
    public async Task Release_FromAnotherSite_LeavesTheSession_ButStillRedirects()
    {
        await using var host = await StartActingAsync();

        var response = await host.PostFormAsync("/kiosk/release", new Dictionary<string, string>(), site: "cross-site");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Wall, response.Headers.Location?.OriginalString);
        Assert.True(host.Has(AuthCookie));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActAs_StillRequiresAValidToken_AndTheTabletGetsAPageBackToTheWall(bool staleToken)
    {
        await using var host = await StartActingAsync();
        var form = new Dictionary<string, string> { ["userId"] = Guid.NewGuid().ToString(), ["pin"] = string.Empty };
        if (staleToken)
        {
            form[TokenField] = await (await host.GetAsync("/form")).Content.ReadAsStringAsync();
            host.Forget(AuthCookie);
        }

        var response = await host.PostFormAsync("/kiosk/act-as", form);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"<meta http-equiv=\"refresh\" content=\"{KioskErrorRecovery.RetrySeconds};url={Wall}\">", body);
        Assert.Contains($"location.replace(\"{Wall}\")", body);
    }

    [Fact]
    public async Task TheWallPageItselfFailing_RetriesSlowly()
    {
        await using var host = await StartActingAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, Wall);
        request.Headers.Add("Sec-Fetch-Dest", "document");

        var response = await host.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains($"content=\"{KioskErrorRecovery.WallRetrySeconds};url={Wall}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnErrorWithoutADeviceCookie_KeepsItsEmptyBody()
    {
        await using var host = await KioskReleaseTestHost.StartAsync();

        var response = await host.PostFormAsync("/kiosk/act-as", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/kiosk/act-as", "document", true)]
    [InlineData("/api/walls/x", "document", false)]
    [InlineData("/_blazor", "document", false)]
    [InlineData("/kiosk/act-as", "empty", false)]
    public async Task OnlyDocumentNavigations_OutsideTheApi_GetTheRecoveryPage(string path, string dest, bool recovered)
    {
        var cookie = new KioskDeviceCookie(new EphemeralDataProtectionProvider());
        var write = new DefaultHttpContext();
        cookie.Write(write, Guid.NewGuid(), Guid.NewGuid());
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(cookie).BuildServiceProvider(),
        };
        http.Request.Path = path;
        http.Request.Headers["Sec-Fetch-Dest"] = dest;
        http.Request.Headers.Cookie = write.Response.Headers.SetCookie.ToString().Split(';')[0];
        http.Response.Body = new MemoryStream();

        await KioskErrorRecovery.RecoverAsync(http);

        Assert.Equal(recovered, http.Response.Body.Length > 0);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("same-origin", null, true)]
    [InlineData("same-site", null, false)]
    [InlineData("cross-site", null, false)]
    [InlineData(null, "https://blocwerk.app", true)]
    [InlineData(null, "https://evil.example", false)]
    [InlineData(null, "null", false)]
    public void SameOriginPost_TrustsFetchMetadataFirst_ThenOrigin(string? site, string? origin, bool expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("blocwerk.app");
        if (site is not null)
        {
            http.Request.Headers["Sec-Fetch-Site"] = site;
        }

        if (origin is not null)
        {
            http.Request.Headers.Origin = origin;
        }

        Assert.Equal(expected, SameOriginPost.IsSameOrigin(http.Request));
    }

    private static async Task<KioskReleaseTestHost> StartActingAsync()
    {
        var host = await KioskReleaseTestHost.StartAsync();
        await host.GetAsync("/device");
        await host.GetAsync("/in");
        Assert.True(host.Has(AuthCookie));
        Assert.True(host.Has(KioskDeviceCookie.Name));
        return host;
    }
}
