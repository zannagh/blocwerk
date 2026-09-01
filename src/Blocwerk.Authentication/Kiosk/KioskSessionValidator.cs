using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Authentication.Kiosk;

/// <summary>
/// The 30-minute idle window for kiosk sessions, enforced where the cookie is read rather than by a
/// timer: <see cref="CookieAuthenticationEvents.OnValidatePrincipal"/>.
/// </summary>
/// <remarks>
/// Check-on-read is how every other expiry in this app works (verification codes, API keys) and there
/// is no background sweeper to add one to. It also has the property that matters here: the session
/// cannot outlive its own next use, so an abandoned tablet is anonymous again the moment anybody
/// touches it after the window.
/// <para>
/// A principal WITHOUT <see cref="KioskClaims.KeyId"/> leaves this method on the first line. Ordinary
/// logins keep the handler's 8-hour sliding window, their claims, and their cookie, untouched.
/// </para>
/// </remarks>
public static class KioskSessionValidator
{
    /// <summary>Wires this validator into the cookie handler's options.</summary>
    public static void Configure(CookieAuthenticationOptions options)
    {
        var inner = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            await inner(context);
            await ValidateAsync(context);
        };
    }

    /// <summary>True when the idle window has elapsed since the last observed activity.</summary>
    public static bool IsIdleExpired(DateTimeOffset lastSeen, DateTimeOffset now)
    {
        return now - lastSeen > KioskClaims.IdleTimeout;
    }

    /// <summary>True when the last-seen stamp is stale enough to be worth rewriting the cookie for.</summary>
    public static bool ShouldSlide(DateTimeOffset lastSeen, DateTimeOffset now)
    {
        return now - lastSeen >= KioskClaims.SlideInterval;
    }

    /// <summary>
    /// Returns a copy of the identity with <see cref="KioskClaims.LastSeen"/> moved to
    /// <paramref name="now"/>, leaving every other claim exactly as it was.
    /// </summary>
    public static ClaimsPrincipal WithLastSeen(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var claims = principal.Claims
            .Where(c => c.Type != KioskClaims.LastSeen)
            .Append(new Claim(KioskClaims.LastSeen, KioskClaims.FormatLastSeen(now)))
            .ToList();

        var source = principal.Identity as ClaimsIdentity;
        var identity = new ClaimsIdentity(
            claims,
            source?.AuthenticationType ?? CookieAuthenticationDefaults.AuthenticationScheme,
            source?.NameClaimType ?? ClaimTypes.Name,
            source?.RoleClaimType ?? ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (!principal.IsKioskPrincipal())
        {
            // Not a kiosk session. Nothing about the normal 8h sliding cookie changes.
            return;
        }

        var keyId = principal.ReadGuid(KioskClaims.KeyId);
        var wallId = principal.ReadGuid(KioskClaims.WallId);
        var userId = principal.ReadGuid("uid");
        var lastSeen = principal.ReadLastSeen();

        // A malformed kiosk claim set is not a normal session that lost a claim — it is a kiosk
        // session we cannot bound. Drop it.
        if (keyId is null || wallId is null || userId is null || lastSeen is null)
        {
            await RejectAsync(context);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (IsIdleExpired(lastSeen.Value, now))
        {
            await RejectAsync(context);
            return;
        }

        // Revoking the kiosk key, or the member withdrawing consent, ends a LIVE session — both are
        // locked product decisions, and both are re-read here on every validation.
        var validator = context.HttpContext.RequestServices.GetRequiredService<KioskKeyValidator>();
        if (!await validator.IsKeyValidAsync(keyId.Value, wallId.Value)
            || !await validator.HasConsentAsync(wallId.Value, userId.Value))
        {
            await RejectAsync(context);
            return;
        }

        if (!ShouldSlide(lastSeen.Value, now))
        {
            return;
        }

        // Slide the window. IssuedUtc/ExpiresUtc are set explicitly rather than left to the handler's
        // sliding logic, so the ticket's own lifetime is the kiosk's 30 minutes and never the
        // handler's 8 hours.
        context.ReplacePrincipal(WithLastSeen(principal!, now));
        context.Properties.IssuedUtc = now;
        context.Properties.ExpiresUtc = now.Add(KioskClaims.IdleTimeout);
        context.ShouldRenew = true;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
