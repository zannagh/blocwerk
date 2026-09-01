using System.Security.Claims;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The kiosk endpoints: registering a tablet to a wall, starting a session as a consenting member,
/// and ending one.
/// </summary>
/// <remarks>
/// All three are anonymous, antiforgery-protected form posts followed by a full-page redirect, in the
/// same shape as the password login. That is not a stylistic choice: the layout is static SSR, the
/// authentication state provider never raises a change notification, and the identity being switched
/// lives in a cookie — so a full navigation is the only way the new identity actually takes effect.
/// <para>
/// The kiosk key is submitted as a FORM FIELD, never as an <c>Authorization</c> header. The scheme
/// selector only forwards <c>bwk_</c> bearers under /api/walls and /api/v1, so a bearer sent here
/// would be silently ignored and the request would look anonymous — a very quiet way to fail open.
/// </para>
/// </remarks>
public sealed class KioskController : Controller
{
    private readonly IApiKeyService apiKeyService;
    private readonly IKioskService kioskService;
    private readonly ICurrentUserService currentUserService;
    private readonly IKioskContext kioskContext;
    private readonly KioskDeviceCookie deviceCookie;
    private readonly KioskKeyValidator keyValidator;
    private readonly KioskThrottleRegistry throttle;
    private readonly KioskPairingRegistry pairings;
    private readonly ILogger<KioskController> logger;

    public KioskController(
        IApiKeyService apiKeyService,
        IKioskService kioskService,
        ICurrentUserService currentUserService,
        IKioskContext kioskContext,
        KioskDeviceCookie deviceCookie,
        KioskKeyValidator keyValidator,
        KioskThrottleRegistry throttle,
        KioskPairingRegistry pairings,
        ILogger<KioskController> logger)
    {
        this.pairings = pairings;
        this.apiKeyService = apiKeyService;
        this.kioskService = kioskService;
        this.currentUserService = currentUserService;
        this.kioskContext = kioskContext;
        this.deviceCookie = deviceCookie;
        this.keyValidator = keyValidator;
        this.throttle = throttle;
        this.logger = logger;
    }

    /// <summary>
    /// Registers this browser as the tablet for the wall the kiosk key belongs to.
    /// </summary>
    [HttpPost("/kiosk/register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register([FromForm] string? key)
    {
        // Registration is the one place a long-lived secret is typed in. It is throttled per client
        // address AND globally — the address is client-controlled here (see
        // KioskThrottleRegistry.RegistrationScopes), so the global cap is the one that actually
        // bounds guessing.
        var throttleScopes = KioskThrottleRegistry.RegistrationScopes(
            HttpContext.Connection.RemoteIpAddress?.ToString());
        if (throttle.IsLocked(throttleScopes))
        {
            return RegistrationFailure(throttleScopes, countFailure: false);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return RegistrationFailure(throttleScopes);
        }

        // The authoritative gate: null for an unknown, revoked, expired, or non-kiosk-scope token.
        var wallId = await apiKeyService.ValidateKioskAsync(key.Trim());
        if (wallId is null)
        {
            return RegistrationFailure(throttleScopes);
        }

        // The cookie has to carry the key's ID so later requests can re-check it without the token,
        // which is never stored. Re-read the same token and insist the two agree.
        var apiKey = await apiKeyService.ValidateAsync(key.Trim());
        if (apiKey is null || apiKey.Scope != Core.Enums.ApiKeyScope.Kiosk || apiKey.WallId != wallId)
        {
            return RegistrationFailure(throttleScopes);
        }

        throttle.Reset(throttleScopes);
        var registered = deviceCookie.Write(HttpContext, apiKey.Id, wallId.Value);
        logger.LogInformation(
            "Kiosk device {DeviceId} registered to wall {WallId} with key {ApiKeyId}",
            registered.DeviceId,
            wallId,
            apiKey.Id);

        return LocalRedirect($"/walls/{wallId}");
    }

    /// <summary>
    /// Finishes a device pairing: redeems the approved pairing with the tablet's claim ticket and
    /// writes the device registration. The tail of <see cref="Register"/>, reached without anybody
    /// having typed a key.
    /// </summary>
    /// <remarks>
    /// <b>Why the tablet has to make this request at all.</b> Everything up to here happened in the
    /// tablet's Blazor circuit, and a circuit cannot write a cookie — there is no response to write
    /// it onto. So the circuit auto-submits a server-rendered form and the registration is written
    /// on the tablet's own connection, which is also what pins the cookie to the right device.
    /// <para>
    /// <b>Why a POST and not a completion GET.</b> The claim ticket is a credential for the two or
    /// three seconds it lives, and a GET would put it in the address bar, in browser history, in the
    /// <c>Referer</c> of whatever loads next, and in the request log of every proxy on the way. It
    /// would also be replayable by anything that speculatively fetches a URL. A form POST keeps it in
    /// a body nobody logs, carries the antiforgery token the rest of this controller already
    /// requires, and matches how every other identity transition in this app works. Replay is
    /// additionally impossible on its own terms: <see cref="KioskPairingRegistry.TryRedeem"/> removes
    /// the entry inside a lock, so the second attempt — whoever makes it — finds nothing.
    /// </para>
    /// </remarks>
    [HttpPost("/kiosk/pair/complete")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompletePairing([FromForm] Guid pairingId, [FromForm] string? ticket)
    {
        // Not throttled, and deliberately so. The ticket is 256 random bits, which is not a guessing
        // target, and the two counters that matter are already upstream: creating the pairing and
        // typing the code. A cap here would only give a stranger a way to lock a tablet out of the
        // pairing it is legitimately holding.
        var redemption = pairings.TryRedeem(pairingId, ticket);
        if (redemption is null)
        {
            // Unknown, expired, still unapproved, wrong ticket, already redeemed — one outcome. The
            // tablet lands back on the pairing page and can ask for a fresh code.
            logger.LogInformation("Kiosk pairing {PairingId} could not be redeemed", pairingId);
            return LocalRedirect("/kiosk/pair?perror=1");
        }

        // Re-check the key before writing the cookie, exactly as Register does with the typed token.
        // The key was minted moments ago, but "moments" is enough: an admin who approves and then
        // immediately revokes from the API key panel would otherwise leave this tablet holding a
        // cookie for a dead key, landing it on a wall page in a broken half-state.
        if (!await keyValidator.IsKeyValidAsync(redemption.ApiKeyId, redemption.WallId))
        {
            logger.LogWarning(
                "Kiosk pairing {PairingId} redeemed key {ApiKeyId} for wall {WallId}, but the key is no longer valid",
                pairingId,
                redemption.ApiKeyId,
                redemption.WallId);
            return LocalRedirect("/kiosk/pair?perror=1");
        }

        var registered = deviceCookie.Write(HttpContext, redemption.ApiKeyId, redemption.WallId);
        logger.LogInformation(
            "Kiosk device {DeviceId} paired to wall {WallId} with key {ApiKeyId}",
            registered.DeviceId,
            redemption.WallId,
            redemption.ApiKeyId);

        return LocalRedirect($"/walls/{redemption.WallId}");
    }

    /// <summary>
    /// Starts a session acting as a consenting member of the kiosk's wall.
    /// </summary>
    [HttpPost("/kiosk/act-as")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ActAs([FromForm] Guid userId, [FromForm] string? pin)
    {
        // The wall comes from the validated device cookie and NOWHERE else. A wall id in the form
        // would let anyone reachable at this endpoint pick a user on any wall they can name.
        var registration = deviceCookie.Read(HttpContext);
        if (registration is null)
        {
            return Forbid();
        }

        if (!await keyValidator.IsKeyValidAsync(registration.ApiKeyId, registration.WallId))
        {
            // The key was revoked or expired: stop pretending this is still a tablet.
            deviceCookie.Clear(HttpContext);
            return Forbid();
        }

        // Counted against the targeted member AND the device, so working round-robin through the
        // picker is not a way to buy more guesses.
        var throttleScopes = KioskThrottleRegistry.PinScopes(
            registration.ApiKeyId,
            registration.DeviceId,
            userId);
        if (throttle.IsLocked(throttleScopes))
        {
            return ActAsFailure(registration.WallId, throttleScopes, countFailure: false);
        }

        // VerifyPinAsync does no authorisation of its own — it trusts its caller to have proven kiosk
        // access, which is exactly what the two checks above did. It returns false for a user who is
        // not a member of THIS wall and for one who never consented, so a user belonging to another
        // wall cannot be picked here.
        if (!await kioskService.VerifyPinAsync(registration.WallId, userId, pin))
        {
            return ActAsFailure(registration.WallId, throttleScopes);
        }

        var user = await currentUserService.GetUserByIdAsync(userId);
        if (user is null)
        {
            return ActAsFailure(registration.WallId, throttleScopes);
        }

        throttle.Reset(throttleScopes);

        var now = DateTimeOffset.UtcNow;

        // The identity claims mirror the password sign-in exactly, so CurrentUserService resolves
        // this session by "uid" the same way and everything downstream — the wall query filter,
        // AuthorizeView, [Authorize], the offline replay controllers — works unchanged. The three
        // kiosk claims are what mark it as a kiosk session everywhere else.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserAuthId),
            new(ClaimTypes.Name, user.UserName),
            new("Name", user.UserName),
            new("uid", user.Id.ToString()),
            new(KioskClaims.KeyId, registration.ApiKeyId.ToString()),
            new(KioskClaims.WallId, registration.WallId.ToString()),
            new(KioskClaims.LastSeen, KioskClaims.FormatLastSeen(now)),
        };

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                // Never persistent: a session cookie on a shared tablet must not survive the browser,
                // and the 30-minute idle window is the ticket's own lifetime.
                IsPersistent = false,
                ExpiresUtc = now.Add(KioskClaims.IdleTimeout),
                AllowRefresh = true,
            });

        logger.LogInformation(
            "Kiosk session started for user {UserId} on wall {WallId} via key {ApiKeyId}",
            user.Id,
            registration.WallId,
            registration.ApiKeyId);

        return LocalRedirect($"/walls/{registration.WallId}");
    }

    /// <summary>
    /// Ends the acting-as session. The DEVICE registration is deliberately left in place, so the
    /// tablet drops back to anonymous kiosk browsing rather than to a stranger's browser.
    /// </summary>
    [HttpPost("/kiosk/release")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Release()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        var registration = deviceCookie.Read(HttpContext);
        return LocalRedirect(registration is null ? "/" : $"/walls/{registration.WallId}");
    }

    /// <summary>
    /// Unregisters the tablet: clears the device cookie and any live session. Physical access to the
    /// device is the authorisation — the same thing that was needed to register it.
    /// </summary>
    [HttpPost("/kiosk/unregister")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unregister()
    {
        if (kioskContext.IsKiosk)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        deviceCookie.Clear(HttpContext);
        return LocalRedirect("/");
    }

    private IActionResult RegistrationFailure(
        IReadOnlyList<KioskThrottleScope> throttleScopes,
        bool countFailure = true)
    {
        if (countFailure)
        {
            throttle.RegisterFailure(throttleScopes);
        }

        // Mirrors the password login's generic failure: back to the sign-in page with a marker, and
        // no hint about which part of the key was wrong.
        return Redirect("/oauth-select?kerror=1");
    }

    private IActionResult ActAsFailure(
        Guid wallId,
        IReadOnlyList<KioskThrottleScope> throttleScopes,
        bool countFailure = true)
    {
        if (countFailure)
        {
            throttle.RegisterFailure(throttleScopes);
        }

        // One generic outcome for "no such member", "never consented" and "wrong PIN", matching the
        // timing equalisation KioskService.VerifyPinAsync already does.
        return LocalRedirect($"/walls/{wallId}?kiosk_pin_error=1");
    }
}
