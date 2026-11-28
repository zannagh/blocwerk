// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// Binds one Sign in with Apple round trip to the browser that started it: the OAuth state and the raw nonce, sealed
/// with the data-protection key ring.
/// </summary>
/// <remarks>
/// Apple returns with a cross-site form POST (<c>response_mode=form_post</c>), which carries no Lax or Strict cookie.
/// This cookie, and only this one, is <c>SameSite=None; Secure</c> so that POST can prove it comes back to the browser
/// that asked. It is checked again, and only then spent, by <c>/account/callback</c>: the GET that completes the
/// sign-in carries the code in its URL, and a URL relayed into another browser must find no cookie there. The
/// <c>__Host-</c> prefix pins it to this host and path, and it lives ten minutes.
/// </remarks>
public sealed class AppleOAuthCookie(IDataProtectionProvider dataProtection, TimeProvider? clock = null)
{
    /// <summary>The cookie's name.</summary>
    public const string Name = "__Host-bw_apple_oauth";

    /// <summary>How long a round trip may take.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private const string Purpose = "Blocwerk.AppleOAuth.v1";

    private readonly IDataProtector protector = dataProtection.CreateProtector(Purpose);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>Writes the cookie for <paramref name="state"/> and <paramref name="rawNonce"/>.</summary>
    public void Write(HttpResponse response, string state, string rawNonce)
    {
        var expires = Clock.GetUtcNow().Add(Ttl).ToUnixTimeSeconds();
        response.Cookies.Append(Name, protector.Protect($"{state}|{rawNonce}|{expires}"), Options(Ttl));
    }

    /// <summary>
    /// The raw nonce of the round trip, when the request carries a sealed, unexpired cookie for exactly
    /// <paramref name="state"/>; otherwise null. Leaves the cookie in place for the callback that completes the trip.
    /// </summary>
    public string? Peek(HttpRequest request, string? state) =>
        request.Cookies.TryGetValue(Name, out var raw) ? Read(raw, state) : null;

    /// <summary>As <see cref="Peek"/>, and deletes the cookie either way, so it completes one round trip.</summary>
    public string? Take(HttpRequest request, HttpResponse response, string? state)
    {
        if (!request.Cookies.TryGetValue(Name, out var raw) || string.IsNullOrEmpty(raw))
        {
            return null;
        }

        Delete(response);
        return Read(raw, state);
    }

    /// <summary>Drops the cookie, for a round trip that ends without completing (a cancel at Apple).</summary>
    public void Delete(HttpResponse response) => response.Cookies.Delete(Name, Options(null));

    private string? Read(string? raw, string? state)
    {
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(state))
        {
            return null;
        }

        try
        {
            var parts = protector.Unprotect(raw).Split('|');
            if (parts.Length != 3
                || !long.TryParse(parts[2], out var expires)
                || DateTimeOffset.FromUnixTimeSeconds(expires) < Clock.GetUtcNow()
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(parts[0]), Encoding.UTF8.GetBytes(state)))
            {
                return null;
            }

            return parts[1];
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static CookieOptions Options(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        IsEssential = true,
        Path = "/",
        MaxAge = maxAge,
    };
}
