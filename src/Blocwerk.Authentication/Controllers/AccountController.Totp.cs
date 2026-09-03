using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// TOTP challenge half of <see cref="AccountController"/>. Sits between a verified password and the
/// actual sign-in: the password endpoint hands off here with a signed, short-lived, single-use
/// "pending" cookie, and only a valid code completes the same sign-in the password path would have.
/// </summary>
public partial class AccountController
{
    private const string TotpPendingPurpose = "Blocwerk.TotpPending.v1";
    private const string TotpPendingCookieName = "bw_totp_pending";

    // A small cap so a stolen/guessed 6-digit code can't be brute-forced within the 5-minute window.
    // The counter rides inside the (signed) cookie, so it can't be reset by clearing client state.
    private const int MaxTotpAttempts = 5;

    private static readonly TimeSpan TotpPendingTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifies the 6-digit challenge code. Reads AND deletes the pending cookie up front (single use),
    /// re-loads the user, and on a valid code completes the same persistent sign-in the password path
    /// builds. Every failure funnels into one generic error that never reveals which step failed; on a
    /// wrong-but-not-exhausted attempt the cookie is re-issued (attempt counter bumped) so a fat-finger
    /// gets another try without a fresh password entry.
    /// </summary>
    [HttpPost("/account/totp/verify")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TotpVerify([FromForm] string? code)
    {
        // Single-use: drop the pending cookie before doing anything so a replay can't reuse it.
        var hadCookie = TryReadTotpPending(out var userId, out var returnUrl, out var attempts, out var isPersistent);
        Response.Cookies.Delete(TotpPendingCookieName, TotpPendingCookieDeleteOptions());

        if (!hadCookie)
        {
            return TotpFailure(returnUrl: null);
        }

        var user = await LoadTotpUserAsync(userId);

        if (user is null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecretProtected))
        {
            return TotpFailure(returnUrl);
        }

        // Server-side lockout is the real brute-force cap here (the cookie attempt counter below is only
        // UX): a persisted per-user lock survives a re-minted or forged cookie and a fresh flow.
        if (await _loginLockout.IsLockedAsync(userId))
        {
            return TotpFailure(returnUrl);
        }

        string secret;
        try
        {
            secret = _totpService.Unprotect(user.TotpSecretProtected);
        }
        catch (Exception)
        {
            return TotpFailure(returnUrl);
        }

        if (_totpService.Verify(secret, code ?? string.Empty, out var matchedStep))
        {
            // Replay guard: reject a still-valid code whose matched step was already used (or is older
            // than) the last successful step. Treated as a failure so it counts toward the lockout.
            if (user.TotpLastUsedStep is { } lastStep && matchedStep <= lastStep)
            {
                await _loginLockout.RegisterFailureAsync(userId);
                return TotpFailure(returnUrl);
            }

            // Success: record the consumed step and clear the failure window in one authoritative write,
            // then complete the same persistent sign-in the password path builds.
            await RecordTotpSuccessAsync(userId, matchedStep);
            return await CompletePasswordSignInAsync(user, returnUrl, isPersistent);
        }

        // Wrong code: count it toward the persisted, per-user lockout (the security cap).
        await _loginLockout.RegisterFailureAsync(userId);

        // Then allow a few more tries within the same window by re-issuing the cookie with the attempt
        // counter advanced (a fat-finger UX affordance only), and bounce back to the challenge with a
        // generic error. Once the cap is hit the bridge is gone (cookie already deleted) and the user
        // must start from the password.
        var nextAttempt = attempts + 1;
        if (nextAttempt < MaxTotpAttempts)
        {
            IssueTotpPendingCookie(userId, returnUrl, isPersistent, nextAttempt);
            var challenge = "/account/totp?terror=1";
            if (!string.IsNullOrEmpty(returnUrl))
            {
                challenge += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
            }

            return Redirect(challenge);
        }

        return TotpFailure(returnUrl);
    }

    /// <summary>
    /// Writes the signed, short-lived pending marker (userId + returnUrl + expiry + attempt count) that
    /// bridges a verified password to the TOTP challenge.
    /// </summary>
    private void IssueTotpPendingCookie(Guid userId, string? returnUrl, bool isPersistent, int attempts = 0)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(TotpPendingTtl).ToUnixTimeSeconds();
        var encodedReturn = Uri.EscapeDataString(returnUrl ?? string.Empty);
        var payload = $"{userId:N}|{encodedReturn}|{expiresAt}|{attempts}|{(isPersistent ? 1 : 0)}";
        Response.Cookies.Append(TotpPendingCookieName, _totpPendingProtector.Protect(payload), TotpPendingCookieOptions());
    }

    /// <summary>
    /// Reads and validates the pending cookie. Returns false (with empty outputs) when it is absent,
    /// tampered, malformed or expired.
    /// </summary>
    private bool TryReadTotpPending(out Guid userId, out string? returnUrl, out int attempts, out bool isPersistent)
    {
        userId = Guid.Empty;
        returnUrl = null;
        attempts = 0;
        isPersistent = false;

        if (!Request.Cookies.TryGetValue(TotpPendingCookieName, out var raw) || string.IsNullOrEmpty(raw))
        {
            return false;
        }

        try
        {
            var decoded = _totpPendingProtector.Unprotect(raw);
            var parts = decoded.Split('|');
            if (parts.Length != 5
                || !Guid.TryParseExact(parts[0], "N", out var parsedUser)
                || !long.TryParse(parts[2], out var expiresAtUnix)
                || !int.TryParse(parts[3], out var parsedAttempts)
                || !int.TryParse(parts[4], out var parsedPersistent))
            {
                return false;
            }

            if (DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix) < DateTimeOffset.UtcNow)
            {
                return false;
            }

            userId = parsedUser;
            var candidate = Uri.UnescapeDataString(parts[1]);

            // Only ever honour a local returnUrl — the same guard the password endpoint applies.
            returnUrl = !string.IsNullOrEmpty(candidate) && Url.IsLocalUrl(candidate) ? candidate : null;
            attempts = parsedAttempts;
            isPersistent = parsedPersistent == 1;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the account the pending marker names, for the second leg of the password challenge.
    /// </summary>
    /// <remarks>
    /// Deleted accounts are filtered out for the same reason
    /// <c>IPasswordLoginService.FindByLoginUsernameAsync</c> filters them on the first leg: the two
    /// legs are separate requests, and an account erased in between must not be able to finish
    /// signing in on the strength of a marker minted moments earlier. A null here is indistinguishable
    /// from a bad code to the caller, which is exactly what TotpFailure already guarantees.
    /// </remarks>
    private async Task<Core.Entities.User?> LoadTotpUserAsync(Guid userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
    }

    // On a successful TOTP verify, persist the consumed time-step (replay guard) and clear the lockout
    // window (any successful auth resets it) in a single tracked write.
    private async Task RecordTotpSuccessAsync(Guid userId, long matchedStep)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (dbUser is null)
        {
            return;
        }

        dbUser.TotpLastUsedStep = matchedStep;
        dbUser.FailedAuthCount = 0;
        dbUser.LockoutUntil = null;
        await db.SaveChangesAsync();
    }

    // Generic challenge failure: back to the sign-in page with the same non-specific error the password
    // path uses, so a caller can never tell a bad code from a lost/expired session.
    private IActionResult TotpFailure(string? returnUrl)
    {
        var target = "/oauth-select?perror=1";
        if (!string.IsNullOrEmpty(returnUrl))
        {
            target += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        return Redirect(target);
    }

    private CookieOptions TotpPendingCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = TotpPendingTtl,
        Path = "/",
    };

    private CookieOptions TotpPendingCookieDeleteOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
    };
}
