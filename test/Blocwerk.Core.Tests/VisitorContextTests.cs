// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Visits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// How a session becomes a visitor: the protected visit cookie, read into <see cref="KioskContext"/> only for a session
/// nobody is signed in to and that is no kiosk tablet, and only until the visit ends.
/// </summary>
public class VisitorContextTests
{
    private static readonly Guid WallId = Guid.NewGuid();

    [Fact]
    public void AnonymousBrowserWithAGrant_IsAVisitorOfThatWallUntilItEnds()
    {
        var clock = new VisitTestClock(VisitFixture.Start);
        var context = Resolve(clock, principal: null, Grant());

        Assert.False(context.IsKiosk);
        Assert.Equal(WallId, context.VisitorWallId);
        Assert.Equal(WallId, KioskViewing.ViewableWallId(context));
        Assert.True(KioskViewing.IsVisitor(context));

        clock.Advance(WallVisitService.Lifetime);

        Assert.Null(context.VisitorWallId);
        Assert.Null(KioskViewing.ViewableWallId(context));
        Assert.NotNull(context.VisitorExpiresAt);
    }

    [Fact]
    public void SignedInBrowser_IgnoresALeftoverGrant()
    {
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", Guid.NewGuid().ToString())], "Cookies"));
        var context = Resolve(new VisitTestClock(VisitFixture.Start), signedIn, Grant());

        Assert.Null(context.VisitorWallId);
        Assert.Null(context.VisitorExpiresAt);
        Assert.False(KioskViewing.IsVisitor(context));
    }

    [Fact]
    public void KioskTablet_IgnoresAGrant()
    {
        var registration = new KioskDeviceRegistration(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VisitFixture.Start);
        var context = Resolve(new VisitTestClock(VisitFixture.Start), principal: null, Grant(), registration);

        Assert.True(context.IsKiosk);
        Assert.Null(context.VisitorWallId);
        Assert.Null(context.VisitorExpiresAt);
        Assert.False(KioskViewing.IsVisitor(context));
    }

    [Fact]
    public void Cookie_RoundTripsAndRefusesATamperedValue()
    {
        var cookie = new VisitGrantCookie(new EphemeralDataProtectionProvider());
        var grant = Grant();
        var response = new DefaultHttpContext();
        cookie.Write(response, grant);
        var header = response.Response.Headers.SetCookie.ToString();

        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        var value = header.Split(';')[0][(VisitGrantCookie.Name.Length + 1)..];

        Assert.Equal(grant, cookie.Read(Request(value)));
        Assert.Null(cookie.Read(Request(value[..^2] + "AA")));
        Assert.Null(cookie.Read(Request("not-a-grant")));
        Assert.Null(new VisitGrantCookie(new EphemeralDataProtectionProvider()).Read(Request(value)));
    }

    private static VisitGrant Grant() =>
        new(Guid.NewGuid(), WallId, Guid.NewGuid(), DateTimeOffset.FromUnixTimeSeconds((VisitFixture.Start + WallVisitService.Lifetime).ToUnixTimeSeconds()));

    private static KioskContext Resolve(
        TimeProvider clock, ClaimsPrincipal? principal, VisitGrant grant, KioskDeviceRegistration? registration = null)
    {
        var context = new KioskContext(
            new HttpContextAccessor(), new KioskDeviceCookie(new EphemeralDataProtectionProvider()), clock: clock);
        context.Apply(principal, registration, grant);
        return context;
    }

    private static HttpContext Request(string cookieValue)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"{VisitGrantCookie.Name}={cookieValue}";
        return http;
    }
}
