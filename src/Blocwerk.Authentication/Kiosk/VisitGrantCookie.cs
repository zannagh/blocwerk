// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Visits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Kiosk;

/// <summary>
/// The visit cookie: "this browser used visitor link L and may view wall W read-only until E". Written once, by the
/// redemption that won the link, and read into <see cref="KioskContext"/> on every request and at circuit start.
/// </summary>
/// <remarks>
/// Protected with the app's DataProtection key ring under a purpose of its own, like the kiosk device cookie, so the
/// browser can neither read nor forge it. HttpOnly, SameSite=Lax, Secure on https, and it expires with the link. The
/// expiry inside the payload is what counts: a value copied off the device stops working at the same moment, whatever
/// its Expires attribute says.
/// </remarks>
public sealed class VisitGrantCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "blocwerk-visit";

    private const string ProtectorPurpose = "blocwerk.visit.grant";
    private const string PayloadVersion = "v1";

    private readonly IDataProtector protector;

    public VisitGrantCookie(IDataProtectionProvider dataProtectionProvider)
    {
        protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>Writes <paramref name="grant"/> onto the response.</summary>
    public void Write(HttpContext context, VisitGrant grant)
    {
        var payload = protector.Protect(
            $"{PayloadVersion}:{grant.LinkId:N}:{grant.WallId:N}:{grant.RedemptionId:N}:{grant.ExpiresAt.ToUnixTimeSeconds()}");
        context.Response.Cookies.Append(Name, payload, BuildOptions(context, grant.ExpiresAt));
    }

    /// <summary>Removes the cookie, for instance once the visit has ended.</summary>
    public void Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(Name, BuildOptions(context, expires: null));
    }

    /// <summary>
    /// The grant on the request, or null when there is none or it is unreadable. An ENDED grant is returned as it is
    /// (<see cref="VisitGrant.IsActiveAt"/> tells), so the session can stay closed after the visit.
    /// </summary>
    public VisitGrant? Read(HttpContext? context)
    {
        if (context is null
            || !context.Request.Cookies.TryGetValue(Name, out var value)
            || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Parse(protector.Unprotect(value));
        }
        catch (Exception)
        {
            // Tampered, or protected with a key ring that is gone: no visit.
            return null;
        }
    }

    private static VisitGrant? Parse(string payload)
    {
        var parts = payload.Split(':');
        if (parts.Length != 5
            || parts[0] != PayloadVersion
            || !Guid.TryParseExact(parts[1], "N", out var linkId)
            || !Guid.TryParseExact(parts[2], "N", out var wallId)
            || !Guid.TryParseExact(parts[3], "N", out var redemptionId)
            || !long.TryParse(parts[4], out var expiresUnixSeconds))
        {
            return null;
        }

        return new VisitGrant(linkId, wallId, redemptionId, DateTimeOffset.FromUnixTimeSeconds(expiresUnixSeconds));
    }

    private static CookieOptions BuildOptions(HttpContext context, DateTimeOffset? expires)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            Expires = expires,
        };
    }
}
