// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Authentication.Services;
using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Helpers;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Why a request carries no session (<see cref="SignOutReason"/>, Phase 3 PR 3.5): <c>expired</c> by default (an expired
/// ticket, no cookie), <c>forced</c> on a validator's rejection, <c>gone</c> on an account that resolves to nobody, and
/// the sticky marker that carries the last two over to the device's later requests until it signs in again.
/// </summary>
public sealed class SignOutReasonTests
{
    [Fact]
    public void NoReason_IsExpired_AndAnExpiredOneSetsNoMarker()
    {
        Assert.Equal(SignOutReasons.Expired, SignOutReason.Of(null));
        var http = new DefaultHttpContext();
        Assert.Equal(SignOutReasons.Expired, SignOutReason.Of(http));

        SignOutReason.Mark(http, SignOutReasons.Expired);

        Assert.Equal(SignOutReasons.Expired, SignOutReason.Of(http));
        Assert.Empty(http.Response.Headers.SetCookie.ToString());
    }

    [Theory]
    [InlineData(SignOutReasons.Forced)]
    [InlineData(SignOutReasons.Gone)]
    public void AForcedOrGoneSignOut_IsThisRequestsReason_AndSetsTheStickyMarker(string reason)
    {
        var http = new DefaultHttpContext();

        SignOutReason.Mark(http, reason);

        Assert.Equal(reason, SignOutReason.Of(http));
        var cookie = http.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{SignOutReason.MarkerCookie}={reason}", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"max-age={(int)SignOutReason.MarkerLifetime.TotalSeconds}", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("forced", SignOutReasons.Forced)]
    [InlineData("gone", SignOutReasons.Gone)]
    [InlineData("expired", SignOutReasons.Expired)]
    [InlineData("anything", SignOutReasons.Expired)]
    public void ALaterRequest_ReadsTheMarker_AndOnlyItsStickyValues(string marker, string expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"{SignOutReason.MarkerCookie}={marker}";

        Assert.Equal(expected, SignOutReason.Of(http));
    }

    [Fact]
    public void Stamp_WritesTheHeader()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"{SignOutReason.MarkerCookie}=gone";

        SignOutReason.Stamp(http);

        Assert.Equal(SignOutReasons.Gone, http.Response.Headers[SignOutReasons.HeaderName].ToString());
    }

    [Fact]
    public async Task ASignIn_DeletesTheMarker()
    {
        var options = new CookieAuthenticationOptions();
        SignOutReason.Configure(options);
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"{SignOutReason.MarkerCookie}=forced";
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));

        await options.Events.SignedIn(new CookieSignedInContext(http, scheme, new ClaimsPrincipal(new ClaimsIdentity("Cookies")), new AuthenticationProperties(), options));

        var cookie = http.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{SignOutReason.MarkerCookie}=;", cookie);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A kiosk session the validator rejects (here a malformed claim set) is a <c>forced</c> sign-out.</summary>
    [Fact]
    public async Task AKioskValidatorRejection_IsForced()
    {
        var options = new CookieAuthenticationOptions();
        KioskSessionValidator.Configure(options);
        var context = ValidationContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(KioskClaims.KeyId, "not-a-guid")], "Cookies")), options);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal(SignOutReasons.Forced, SignOutReason.Of(context.HttpContext));
        Assert.Contains($"{SignOutReason.MarkerCookie}=forced", context.HttpContext.Response.Headers.SetCookie.ToString());
    }

    /// <summary>An ordinary session the validators leave alone has no reason recorded.</summary>
    [Fact]
    public async Task AnOrdinarySession_RecordsNoReason()
    {
        var options = new CookieAuthenticationOptions();
        KioskSessionValidator.Configure(options);
        var context = ValidationContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", Guid.NewGuid().ToString())], "Cookies")), options);

        await options.Events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.DoesNotContain(SignOutReason.MarkerCookie, context.HttpContext.Response.Headers.SetCookie.ToString());
    }

    /// <summary>
    /// A session whose uid names no row (merged away, erased) is <c>gone</c>: the device drops that account's data, not
    /// lock it for a return that cannot happen.
    /// </summary>
    [Fact]
    public async Task ASessionNamingNobody_IsGone()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "merged-subject"),
                new Claim(ClaimTypes.Name, "Merged"),
                new Claim("uid", Guid.NewGuid().ToString()),
                new Claim(AuthFreshness.ClaimType, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            ], "Cookies")),
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        var service = new CurrentUserService(
            new BlocwerkSettings(), harness.DbContextFactory, Substitute.For<IPasswordLoginService>(), Substitute.For<ITotpService>(), accessor: accessor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCurrentUserAsync());

        Assert.Equal(SignOutReasons.Gone, SignOutReason.Of(http));
        Assert.Contains($"{SignOutReason.MarkerCookie}=gone", http.Response.Headers.SetCookie.ToString());
    }

    /// <summary>The feed's own 401 (the docs index, the marks, the feed) says why, in its body and the header; 403/410 do not.</summary>
    [Theory]
    [InlineData(FeedAccessRefusal.Unauthenticated, 401, "gone")]
    [InlineData(FeedAccessRefusal.NoAccess, 403, null)]
    [InlineData(FeedAccessRefusal.AccountGone, 410, null)]
    public async Task TheFeedRefusal_CarriesTheReasonOnA401Only(FeedAccessRefusal refusal, int status, string? reason)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        http.Request.Headers.Cookie = $"{SignOutReason.MarkerCookie}=gone";

        await FeedRequestAccess.Refused(http, refusal).ExecuteAsync(http);

        Assert.Equal(status, http.Response.StatusCode);
        using var body = JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray());
        Assert.Equal(reason, body.RootElement.TryGetProperty("reason", out var value) ? value.GetString() : null);
        Assert.Equal(reason ?? string.Empty, http.Response.Headers[SignOutReasons.HeaderName].ToString());
    }

    private static CookieValidatePrincipalContext ValidationContext(ClaimsPrincipal principal, CookieAuthenticationOptions options)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication().AddCookie();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), scheme.Name);
        return new CookieValidatePrincipalContext(http, scheme, options, ticket);
    }
}
