using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// Password-login endpoint. Authenticates an EXISTING user (one created via OAuth) by their chosen
/// login username + password. It never creates a user and never reveals whether a username exists.
/// </summary>
public partial class AccountController
{
    [HttpPost("/account/password")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PasswordLogin(
        [FromForm] string? username,
        [FromForm] string? password,
        [FromForm] bool keepSignedIn = false,
        [FromForm] string? returnUrl = null)
    {
        // Only ever honour a local returnUrl (LocalRedirect guards the return leg too).
        if (!string.IsNullOrEmpty(returnUrl) && !Url.IsLocalUrl(returnUrl))
        {
            returnUrl = null;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return PasswordFailure(returnUrl);
        }

        // Look up ONLY existing users that have a password configured. A missing user and a wrong
        // password funnel into the exact same generic failure below — no user enumeration.
        var user = await _passwordLoginService.FindByLoginUsernameAsync(username);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            // Timing equaliser: spend the same PBKDF2 time on the "no such user / no password" miss path
            // as a real wrong-password check, so response time can't reveal which usernames exist.
            _passwordService.Verify(DummyPasswordHash, password);
            return PasswordFailure(returnUrl);
        }

        // Server-side lockout is checked BEFORE the password verify — and before any reset — so an
        // attacker who knows the password can't clear the TOTP failure counter by re-authenticating while
        // the account is locked. This is the real brute-force cap; the TOTP cookie is only a flow bridge.
        if (await _loginLockout.IsLockedAsync(user.Id))
        {
            return PasswordFailure(returnUrl);
        }

        if (!_passwordService.Verify(user.PasswordHash, password))
        {
            await _loginLockout.RegisterFailureAsync(user.Id);
            return PasswordFailure(returnUrl);
        }

        // Any successful password auth clears the failure window (the lock check above already guaranteed
        // the account wasn't locked when we got here).
        await _loginLockout.ResetAsync(user.Id);

        // TOTP SEAM: the password is now verified. If the user has a second factor enabled, do NOT sign
        // in here — stash a signed, single-use "password ok, awaiting TOTP" marker (userId + returnUrl)
        // and hand off to the TOTP challenge, which completes the SAME sign-in once a code checks out.
        // This marker is the ONLY bridge to the challenge: no code is accepted without a password success.
        if (user.TotpEnabled)
        {
            IssueTotpPendingCookie(user.Id, returnUrl, keepSignedIn);
            return Redirect("/account/totp");
        }

        return await CompletePasswordSignInAsync(user, returnUrl, keepSignedIn);
    }

    // A fixed dummy hash so the "unknown user / no password" miss path spends the same PBKDF2 time as a
    // real wrong-password verify. Computed once (lazily) from the same hasher the real hashes use, so its
    // work factor stays in lockstep with production hashes.
    private static string? dummyPasswordHash;

    private string DummyPasswordHash => dummyPasswordHash ??= _passwordService.Hash("timing-equaliser-dummy");

    /// <summary>
    /// Signs the user in with the password login's cookie principal + persistent properties. Shared by
    /// the no-second-factor path above and the successful TOTP challenge, so both produce the exact same
    /// session.
    /// </summary>
    internal async Task<IActionResult> CompletePasswordSignInAsync(
        Core.Entities.User user, string? returnUrl, bool isPersistent)
    {
        // Build a cookie principal carrying the exact user id as a "uid" claim, so CurrentUserService
        // resolves this session by id (path 0) — precise, and never misresolving or creating a blank user
        // when the display name contains "__". The NameIdentifier/Name claims stay for the legacy
        // identifier path and for anything that reads the display name off the principal.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserAuthId),
            new(ClaimTypes.Name, user.UserName),
            new("Name", user.UserName),
            new("uid", user.Id.ToString()),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // "Keep me signed in" → a persistent, long-lived (1-year absolute) cookie that overrides the cookie
        // handler's 8h sliding default. Unchecked → a session cookie that ends when the browser closes (the
        // handler's sliding window still bounds the ticket). OAuth logins are unaffected either way.
        var authProperties = isPersistent
            ? new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddYears(1),
                AllowRefresh = true,
            }
            : new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true,
            };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties);

        // Mark this device as a returning visitor so "/" skips the Get Started landing next time. Shared
        // by the no-second-factor path and the successful TOTP challenge, so every password sign-in sets
        // it. (Provider-usage counting is OAuth-only and stays in Callback.)
        SetReturningVisitorCookie();

        return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/walls" : returnUrl);
    }

    // Generic failure: back to the sign-in page with a non-specific error. Never distinguishes
    // "no such user" from "wrong password".
    private IActionResult PasswordFailure(string? returnUrl)
    {
        var target = "/oauth-select?perror=1";
        if (!string.IsNullOrEmpty(returnUrl))
        {
            target += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        return Redirect(target);
    }
}
