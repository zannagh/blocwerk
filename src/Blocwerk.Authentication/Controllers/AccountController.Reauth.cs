using Blocwerk.Authentication.Kiosk;
using Blocwerk.Authentication.Resources;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// The step-up half of <see cref="AccountController"/>: a signed-in user proves, by signing in with
/// their provider again right now, that they are still the person behind the session — the gate an
/// OAuth-only account faces before an irreversible action.
/// </summary>
/// <remarks>
/// Shaped exactly like the account-link round trip next door (a signed, short-lived, single-use
/// intent cookie riding alongside the ordinary OAuth flow), because it is the same round trip asking
/// a different question: link asks "whose is this identity", step-up asks "is this identity yours".
/// Nothing is ever linked, merged or signed in here.
/// </remarks>
public partial class AccountController
{
    private const string ReauthIntentPurpose = "Blocwerk.AccountReauth.v1";
    private const string ReauthIntentCookieName = "bw_reauth_intent";
    private static readonly TimeSpan ReauthIntentTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Starts a step-up round trip for the signed-in user against <paramref name="provider"/>.
    /// </summary>
    /// <remarks>
    /// A POST with an antiforgery token rather than a link, because a GET here was reachable by a
    /// top-level cross-site navigation: any page could make a signed-in victim's browser stash a
    /// step-up intent and bounce through their provider. It issues nothing an attacker can read, but
    /// planting an intent on somebody else's browser is not something a third-party page gets to do.
    /// </remarks>
    [HttpPost(AccountReauthRoutes.StartPath)]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reauth([FromForm] string provider)
    {
        if (GetProviderAuthConfig(provider) is null)
        {
            return Redirect($"{AccountReauthRoutes.DeleteAccountPath}?reauth_error=unavailable");
        }

        // The same refusal the link path makes, for the same reason: a tablet in a gym must never be
        // able to satisfy an account-level proof on behalf of whoever it currently acts as.
        if (KioskRestrictions.IsBlockedAccountLink(_kioskContext))
        {
            return Redirect("/walls?kiosk_blocked=1");
        }

        User currentUser;
        try
        {
            currentUser = await _currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return Redirect("/account/login");
        }

        // The intent is bound to THIS round trip's OAuth state, so it can only ever claim the
        // callback it started. Without that binding an abandoned step-up sat in the cookie jar for ten
        // minutes and swallowed the next callback of any kind — an account-link in another tab came
        // back as a step-up mismatch and was silently lost.
        var state = Guid.NewGuid().ToString();

        var expiresAt = DateTimeOffset.UtcNow.Add(ReauthIntentTtl).ToUnixTimeSeconds();
        var payload = $"{currentUser.Id:N}|{provider}|{expiresAt}|{state}";
        Response.Cookies.Append(ReauthIntentCookieName, _reauthProtector.Protect(payload), ReauthIntentCookieOptions());

        _redirectUriProvider.AddRedirectUri(state, new RedirectSettings
        {
            Uri = $"{BaseUrl}/account/callback",
            Provider = provider,
        });

        return RedirectToProviderOr(provider, state, $"{AccountReauthRoutes.DeleteAccountPath}?reauth_error=unavailable");
    }

    /// <summary>
    /// The step-up branch of /account/callback: the returned provider identity must belong to the
    /// account that is still signed in. On a match a single-use ticket is issued and handed back to
    /// the page; on anything else nothing is issued and the page stays gated.
    /// </summary>
    private async Task<IActionResult> HandleReauthCallbackAsync(string code, Guid reauthUserId)
    {
        // Single-use: drop the intent up front so a replayed callback cannot mint a second ticket.
        Response.Cookies.Delete(ReauthIntentCookieName, ReauthIntentCookieDeleteOptions());

        if (KioskRestrictions.IsBlockedAccountLink(_kioskContext))
        {
            return Redirect("/walls?kiosk_blocked=1");
        }

        User currentUser;
        try
        {
            currentUser = await _currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return Redirect("/account/login?error=reauth_session_lost");
        }

        // The session must still be the one that asked. A mismatch means it changed mid-round-trip.
        if (currentUser.Id != reauthUserId)
        {
            Log.Warning(
                "[Web Authentication] Step-up aborted: stashed user {StashedUser} != current user {CurrentUser}.",
                reauthUserId,
                currentUser.Id);
            return Redirect($"{AccountReauthRoutes.DeleteAccountPath}?reauth_error=mismatch");
        }

        var info = await ExchangeCodeForIdentityAsync(code);
        if (info is null)
        {
            return Redirect($"{AccountReauthRoutes.DeleteAccountPath}?reauth_error=failed");
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var owner = await LegacyIdentityResolver.FindByProviderIdentityAsync(
            db,
            info.Value.Provider,
            info.Value.ProviderUserId);

        // Signing in as SOMEBODY ELSE proves nothing about this account. The identity has to be one
        // the signed-in account owns — through a UserIdentity row or, for a pre-UserIdentities
        // account, through the subject recorded in its identifier.
        if (owner is null || owner.Id != currentUser.Id)
        {
            Log.Warning(
                "[Web Authentication] Step-up refused for user {UserId}: the returned identity is not theirs.",
                currentUser.Id);
            return Redirect($"{AccountReauthRoutes.DeleteAccountPath}?reauth_error=not_yours");
        }

        var ticket = _reauthTicketStore.Issue(currentUser.Id);
        return Redirect(
            $"{AccountReauthRoutes.DeleteAccountPath}?{AccountReauthRoutes.TicketQueryParameter}={Uri.EscapeDataString(ticket)}");
    }

    /// <summary>
    /// Reads and validates the step-up intent cookie against the callback it arrived on. Returns
    /// false — leaving every other callback path untouched — when it is absent, tampered, malformed,
    /// expired, or belongs to a DIFFERENT round trip.
    /// </summary>
    /// <remarks>
    /// The state check is what keeps an abandoned step-up from claiming somebody else's callback. A
    /// mismatched intent is deliberately left in place rather than deleted: the tab that started it
    /// may still come back and finish, and it expires on its own within ten minutes either way.
    /// </remarks>
    private bool TryReadReauthIntent(string callbackState, out Guid userId)
    {
        userId = Guid.Empty;

        if (string.IsNullOrEmpty(callbackState))
        {
            return false;
        }

        if (!Request.Cookies.TryGetValue(ReauthIntentCookieName, out var raw) || string.IsNullOrEmpty(raw))
        {
            return false;
        }

        try
        {
            var decoded = _reauthProtector.Unprotect(raw);
            var parts = decoded.Split('|');
            if (parts.Length != 4
                || !Guid.TryParseExact(parts[0], "N", out var parsedUser)
                || !long.TryParse(parts[2], out var expiresAtUnix))
            {
                return false;
            }

            if (!string.Equals(parts[3], callbackState, StringComparison.Ordinal))
            {
                return false;
            }

            if (DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix) < DateTimeOffset.UtcNow)
            {
                return false;
            }

            userId = parsedUser;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private CookieOptions ReauthIntentCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = ReauthIntentTtl,
        Path = "/",
    };

    private CookieOptions ReauthIntentCookieDeleteOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
    };
}
