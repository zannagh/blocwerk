// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Apple;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>
/// The cookie that binds Apple's cross-site form POST to the browser that started the round trip: SameSite=None and
/// Secure (the only such cookie), sealed, bound to one state, short-lived and single-use.
/// </summary>
public class AppleOAuthCookieTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheCookie_IsHostPrefixed_SameSiteNone_Secure_HttpOnly_AndSealsNoPlainNonce()
    {
        var cookies = new AppleOAuthCookie(new EphemeralDataProtectionProvider());
        var response = new DefaultHttpContext().Response;

        cookies.Write(response, "state-1", "raw-nonce-value");

        var header = response.Headers.SetCookie.ToString();
        Assert.StartsWith("__Host-bw_apple_oauth=", header);
        Assert.Contains("samesite=none", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-nonce-value", header);
        Assert.DoesNotContain("state-1", header);
    }

    [Fact]
    public void TheCookie_GivesTheNonce_OnlyForItsOwnState_AndOnlyOnce()
    {
        var cookies = new AppleOAuthCookie(new EphemeralDataProtectionProvider(), new MutableTestClock(Start));
        var sealedValue = Written(cookies, "state-1", "nonce-1");

        Assert.Null(Take(cookies, sealedValue, "state-2", out var deleted));
        Assert.True(deleted, "a cookie for another round trip is spent too");
        Assert.Equal("nonce-1", Take(cookies, sealedValue, "state-1", out deleted));
        Assert.True(deleted);
    }

    [Fact]
    public void AnExpiredTamperedOrMissingCookie_GivesNothing()
    {
        var clock = new MutableTestClock(Start);
        var cookies = new AppleOAuthCookie(new EphemeralDataProtectionProvider(), clock);
        var sealedValue = Written(cookies, "state-1", "nonce-1");

        Assert.Null(Take(cookies, sealedValue[..^2] + "xx", "state-1", out _));
        Assert.Null(Take(cookies, null, "state-1", out _));
        Assert.Null(Take(new AppleOAuthCookie(new EphemeralDataProtectionProvider()), sealedValue, "state-1", out _));

        clock.Advance(AppleOAuthCookie.Ttl + TimeSpan.FromSeconds(1));
        Assert.Null(Take(cookies, sealedValue, "state-1", out _));
    }

    private static string Written(AppleOAuthCookie cookies, string state, string nonce)
    {
        var response = new DefaultHttpContext().Response;
        cookies.Write(response, state, nonce);
        var header = response.Headers.SetCookie.ToString();
        var value = header[(AppleOAuthCookie.Name.Length + 1)..header.IndexOf(';', StringComparison.Ordinal)];
        return Uri.UnescapeDataString(value);
    }

    private static string? Take(AppleOAuthCookie cookies, string? value, string state, out bool deleted)
    {
        var http = new DefaultHttpContext();
        if (value is not null)
        {
            http.Request.Headers.Cookie = $"{AppleOAuthCookie.Name}={Uri.EscapeDataString(value)}";
        }

        var nonce = cookies.Take(http.Request, http.Response, state);
        deleted = http.Response.Headers.SetCookie.ToString().Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
        return nonce;
    }
}
