// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Grades;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The grade-scale cookie the offline client reads (<see cref="GradePreferenceCookie"/>, Phase 3 §2): a signed-in
/// person's preference on Server pages, and never on a kiosk session, nobody picked or acting as a member (PR 3.15),
/// where a cookie the browser had is deleted.
/// </summary>
public sealed class GradePreferenceCookieTests
{
    [Theory]
    [InlineData(false, true, "font")]
    [InlineData(false, false, "v")]
    [InlineData(false, null, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, null, null)]
    public void TheValue_IsThePersonsPreference_AndNoneOnAKiosk(bool isKiosk, bool? preferFont, string? expected)
    {
        Assert.Equal(expected, GradePreferenceCookie.ValueFor(isKiosk, preferFont));
    }

    [Fact]
    public void AValue_IsSetForTheWholeSite_ReadableByTheClient()
    {
        var http = new DefaultHttpContext();

        GradePreferenceCookie.Apply(http, GradePreferenceCookie.VScale);

        var header = SetCookie(http);
        Assert.StartsWith("blocwerk-grade-pref=v;", header, StringComparison.Ordinal);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=31536000", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("httponly", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoValue_DeletesTheCookieTheBrowserHad_AndSetsNothingOtherwise()
    {
        var had = new DefaultHttpContext();
        had.Request.Headers.Cookie = "blocwerk-grade-pref=font";
        GradePreferenceCookie.Apply(had, null);
        var header = SetCookie(had);
        Assert.StartsWith("blocwerk-grade-pref=;", header, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", header, StringComparison.OrdinalIgnoreCase);

        var clean = new DefaultHttpContext();
        GradePreferenceCookie.Apply(clean, null);
        Assert.Equal(0, clean.Response.Headers.SetCookie.Count);
    }

    [Fact]
    public void OnlyAGetOfARazorPage_IsAServerPage()
    {
        Assert.True(GradePreferenceCookieMiddleware.IsServerPage(Request("GET", page: true)));
        Assert.False(GradePreferenceCookieMiddleware.IsServerPage(Request("POST", page: true)));
        Assert.False(GradePreferenceCookieMiddleware.IsServerPage(Request("GET", page: false)));
        Assert.False(GradePreferenceCookieMiddleware.IsServerPage(new DefaultHttpContext()));
    }

    private static string SetCookie(HttpContext http) => Assert.Single(http.Response.Headers.SetCookie.ToArray())!;

    private static DefaultHttpContext Request(string method, bool page)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        var metadata = page ? new EndpointMetadataCollection(new ComponentTypeMetadata(typeof(object))) : new EndpointMetadataCollection();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        return http;
    }
}
