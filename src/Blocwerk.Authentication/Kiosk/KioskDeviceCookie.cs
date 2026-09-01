using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Kiosk;

/// <summary>
/// The device registration cookie: "this browser is the tablet bolted to wall X, registered with
/// kiosk key K". It is deliberately SEPARATE from the auth cookie, because the two have opposite
/// lifetimes — the device registration must survive reboots and outlive every 30-minute acting-as
/// session, and signing out must not unregister the tablet.
/// </summary>
/// <remarks>
/// The payload is protected with the app's persisted DataProtection key ring (the same ring the auth
/// cookie, the TOTP secrets and the TopLogger tokens use, so registrations survive a redeploy), which
/// makes it unforgeable and unreadable by the client. It is nonetheless treated as a CLAIM, not as
/// proof: every use re-checks the referenced key against the database, so revoking the kiosk key kills
/// every device carrying it.
/// <para>
/// The payload carries two things beyond the key and the wall. An ISSUE TIMESTAMP, because
/// unforgeable is not the same as un-replayable: the browser honours the cookie's Expires attribute,
/// but a value copied out of a device once is otherwise a bearer token good for ever, and the
/// timestamp is what lets the server refuse an implausibly old one. And a random DEVICE ID, so two
/// tablets registered with the same kiosk key are distinguishable in logs, and so a per-device
/// revocation list has something to key on if one is ever added. No such store exists today —
/// revocation is still all-or-nothing at the KEY, via <see cref="KioskKeyValidator"/>.
/// </para>
/// </remarks>
public sealed class KioskDeviceCookie
{
    /// <summary>The cookie name. Not prefixed with a dot: it is ours, not a framework cookie.</summary>
    public const string Name = "blocwerk-kiosk";

    private const string ProtectorPurpose = "blocwerk.kiosk.device";

    /// <summary>
    /// Payload format marker. A cookie written before the timestamp and device id existed has no
    /// marker, parses as the wrong field count and is discarded — the tablet is simply unregistered
    /// and can be registered again with the key. That is the intended migration: the alternative is
    /// accepting a payload whose age cannot be checked, which is the thing being fixed.
    /// </summary>
    private const string PayloadVersion = "v2";

    /// <summary>
    /// Tolerance for a clock that moved backwards. A cookie stamped further into the future than
    /// this is nonsense and is refused rather than trusted.
    /// </summary>
    private static readonly TimeSpan FutureSkew = TimeSpan.FromDays(1);

    /// <summary>
    /// Five years. A wall tablet is registered once, by hand, by a wall admin; asking somebody to
    /// walk over with the key again because a cookie aged out is the wrong trade. Revocation of the
    /// key — checked on every use — is the real off switch.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365 * 5);

    private readonly IDataProtector protector;

    public KioskDeviceCookie(IDataProtectionProvider dataProtectionProvider)
    {
        protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>Writes the registration onto the response, and returns what was written.</summary>
    public KioskDeviceRegistration Write(HttpContext context, Guid apiKeyId, Guid wallId)
    {
        var now = DateTimeOffset.UtcNow;
        var registration = new KioskDeviceRegistration(apiKeyId, wallId, Guid.NewGuid(), now);

        var payload = protector.Protect(
            $"{PayloadVersion}:{apiKeyId:N}:{wallId:N}:{registration.DeviceId:N}:{now.ToUnixTimeSeconds()}");

        context.Response.Cookies.Append(Name, payload, BuildOptions(context, now.Add(Lifetime)));
        return registration;
    }

    /// <summary>Clears the registration, returning the tablet to an ordinary browser.</summary>
    public void Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(Name, BuildOptions(context, expires: null));
    }

    /// <summary>
    /// Reads the registration off the request, or null when there is none, it is unreadable, or it
    /// was written with a key ring this app no longer has.
    /// </summary>
    public KioskDeviceRegistration? Read(HttpContext? context)
    {
        return Read(context, DateTimeOffset.UtcNow);
    }

    /// <summary>Testable overload of <see cref="Read(HttpContext?)"/> with an explicit clock.</summary>
    internal KioskDeviceRegistration? Read(HttpContext? context, DateTimeOffset now)
    {
        if (context is null
            || !context.Request.Cookies.TryGetValue(Name, out var value)
            || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var parts = protector.Unprotect(value).Split(':');
            if (parts.Length != 5
                || parts[0] != PayloadVersion
                || !Guid.TryParseExact(parts[1], "N", out var apiKeyId)
                || !Guid.TryParseExact(parts[2], "N", out var wallId)
                || !Guid.TryParseExact(parts[3], "N", out var deviceId)
                || !long.TryParse(parts[4], out var issuedUnixSeconds))
            {
                return null;
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedUnixSeconds);

            // The cookie's Expires attribute is a request to the BROWSER; a value lifted off a device
            // is not bound by it. Checking the age server-side is what actually bounds a copied
            // registration, and a stamp from the future is a sign of nothing good either way.
            if (now - issuedAt > Lifetime || issuedAt - now > FutureSkew)
            {
                return null;
            }

            return new KioskDeviceRegistration(apiKeyId, wallId, deviceId, issuedAt);
        }
        catch (Exception)
        {
            // Tampered, or protected with a key ring that is gone. Either way the tablet is simply
            // not registered; it can be registered again with the key.
            return null;
        }
    }

    private static CookieOptions BuildOptions(HttpContext context, DateTimeOffset? expires)
    {
        return new CookieOptions
        {
            HttpOnly = true,

            // Lax, not Strict: the tablet lands back on the app from an OAuth/redirect leg, and a
            // Strict cookie is withheld on that first cross-site navigation, which would look like a
            // spontaneous unregistration. Lax still withholds it from cross-site POSTs.
            SameSite = SameSiteMode.Lax,

            // Secure whenever the request itself is https, so development over plain http still
            // works while production (always https behind the proxy) always gets the flag.
            Secure = context.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            Expires = expires,
        };
    }
}

/// <summary>A tablet's device registration, as carried by <see cref="KioskDeviceCookie"/>.</summary>
/// <param name="ApiKeyId">The kiosk key this tablet was registered with.</param>
/// <param name="WallId">The one wall it is registered to.</param>
/// <param name="DeviceId">
/// Random per-registration identifier. Identifies the physical tablet in logs and gives a future
/// per-device revocation store something to key on; nothing authorises on it today.
/// </param>
/// <param name="IssuedAt">When the registration was written. Bounds a copied cookie's usefulness.</param>
public sealed record KioskDeviceRegistration(Guid ApiKeyId, Guid WallId, Guid DeviceId, DateTimeOffset IssuedAt);
