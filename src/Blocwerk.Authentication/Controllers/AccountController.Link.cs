using Blocwerk.Authentication.Kiosk;
using Blocwerk.Authentication.Resources;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// Account linking / merge half of <see cref="AccountController"/>. A signed-in user attaches an
/// additional OAuth provider identity to their account; if that provider identity already belongs to a
/// second account, that account is merged into the one they are signed in as.
/// </summary>
public partial class AccountController
{
    private const string LinkIntentPurpose = "Blocwerk.AccountLink.v1";
    private const string LinkIntentCookieName = "bw_link_intent";
    private static readonly TimeSpan LinkIntentTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Starts an account-link round-trip for the signed-in user. Stashes a signed, short-lived,
    /// single-use "link intent" (who + which provider) in a cookie that rides alongside the normal
    /// OAuth flow, then redirects to the provider exactly as a login would — /account/callback reads
    /// the cookie and links instead of signing in.
    /// </summary>
    [HttpGet("/account/link")]
    [Authorize]
    public async Task<IActionResult> Link([FromQuery] string provider)
    {
        if (GetProviderAuthConfig(provider) is null)
        {
            return Redirect("/profile?link=unavailable");
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

        var expiresAt = DateTimeOffset.UtcNow.Add(LinkIntentTtl).ToUnixTimeSeconds();
        var payload = $"{currentUser.Id:N}|{provider}|{expiresAt}";
        Response.Cookies.Append(LinkIntentCookieName, _linkProtector.Protect(payload), LinkIntentCookieOptions());

        var state = Guid.NewGuid().ToString();
        _redirectUriProvider.AddRedirectUri(state, new RedirectSettings
        {
            Uri = $"{BaseUrl}/account/callback",
            Provider = provider,
        });

        return RedirectToProviderOr(provider, state, "/profile?link=unavailable");
    }

    /// <summary>
    /// The account-link branch of /account/callback: resolves the returned provider identity and links
    /// it, reports it already linked, or merges the other account into the current one. Never signs in
    /// as the provider identity.
    /// </summary>
    private async Task<IActionResult> HandleLinkCallbackAsync(string code, Guid linkUserId, string linkProvider)
    {
        // Single-use: drop the intent cookie up front so a replay can't re-trigger a link/merge.
        Response.Cookies.Delete(LinkIntentCookieName, LinkIntentCookieDeleteOptions());

        // A kiosk session may not attach a provider identity — a permanent takeover that would
        // outlive the 30 minutes by years. This refusal lives HERE rather than on the route, because
        // /account/callback also completes ordinary OAuth sign-in and a wall admin must still be able
        // to sign in at the tablet. Nothing has been linked at this point, and the intent cookie is
        // already gone, so the flow simply stops.
        if (KioskRestrictions.IsBlockedAccountLink(_kioskContext))
        {
            Log.Warning(
                "[Web Authentication] Account-link refused for user {UserId}: kiosk session.",
                linkUserId);
            return KioskLinkRefused();
        }

        User currentUser;
        try
        {
            currentUser = await _currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return Redirect("/account/login?error=link_session_lost");
        }

        // The user must still be signed in as the SAME account that started the link. A mismatch means
        // the session changed mid-round-trip — refuse rather than link to the wrong account.
        if (currentUser.Id != linkUserId)
        {
            Log.Warning(
                "[Web Authentication] Account-link aborted: stashed user {StashedUser} != current user {CurrentUser}.",
                linkUserId,
                currentUser.Id);
            return Redirect("/profile?link=error");
        }

        var info = await ExchangeCodeForIdentityAsync(code);
        if (info is null)
        {
            return Redirect("/profile?link=error");
        }

        // Trust the provider re-derived by /token; fall back to the stashed provider if absent.
        var provider = string.IsNullOrEmpty(info.Value.Provider) ? linkProvider : info.Value.Provider;
        var providerUserId = info.Value.ProviderUserId;

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var existing = await db.UserIdentities
            .FirstOrDefaultAsync(i => i.Provider == provider && i.ProviderUserId == providerUserId);

        if (existing is not null)
        {
            if (existing.UserId == currentUser.Id)
            {
                return Redirect("/profile?link=already");
            }

            // The provider identity belongs to a SECOND account: absorb it into the current one.
            await _mergeService.MergeUsersAsync(existing.UserId, currentUser.Id);
            _currentUserService.InvalidateCache();
            Log.Information(
                "[Web Authentication] Merged account {Source} into {Target} via {Provider} link.",
                existing.UserId,
                currentUser.Id,
                provider);
            return Redirect("/profile?link=merged");
        }

        // No UserIdentity row yet. A pre-UserIdentities (legacy) account may still OWN this provider
        // subject, recorded only as the "__{sub}" suffix of its Identifier and therefore invisible to the
        // query above. Detect that duplicate via the SAME resolver login uses so a link can never
        // silently fork the user's history into a second account.
        var legacyOwner = await LegacyIdentityResolver.FindByLegacyIdentifierAsync(db, providerUserId);
        if (legacyOwner is not null && legacyOwner.Id != currentUser.Id)
        {
            // Absorb the legacy duplicate INTO the current (surviving) account, then record the identity
            // on the survivor. Merge direction always keeps the account the user is actively signed in as.
            await _mergeService.MergeUsersAsync(legacyOwner.Id, currentUser.Id);
            _currentUserService.InvalidateCache();
            Log.Information(
                "[Web Authentication] Merged legacy account {Source} into {Target} while linking {Provider} (matched by subject).",
                legacyOwner.Id,
                currentUser.Id,
                provider);

            // The merge already moved the legacy account onto the survivor, so recording the identity is
            // the tail of a "merged" outcome regardless of whether a concurrent request beat us to the row.
            await UserIdentityLinker.EnsureLinkedAsync(db, currentUser.Id, provider, providerUserId);
            return Redirect("/profile?link=merged");
        }

        // Either the provider subject is truly new, or the legacy owner IS the current user (back-fill
        // the missing identity row). Both simply record the identity on the current account. The pre-check
        // above already handled a row that existed at check time; EnsureLinkedAsync additionally closes the
        // window where a concurrent link inserts between that check and this write (unique-violation 23505),
        // resolving it to a clean outcome instead of a 500.
        var result = await UserIdentityLinker.EnsureLinkedAsync(db, currentUser.Id, provider, providerUserId);
        return result switch
        {
            IdentityLinkResult.LinkedToDifferentUser => Redirect("/profile?link=error"),
            IdentityLinkResult.AlreadyLinkedToUser => Redirect("/profile?link=already"),
            _ => Redirect("/profile?link=linked"),
        };
    }

    /// <summary>
    /// Where a kiosk session lands after a refused link. /profile is off limits from a tablet, so it
    /// goes back to the wall with the same marker the kiosk middleware uses for a blocked page.
    /// </summary>
    private IActionResult KioskLinkRefused()
    {
        if (_kioskContext.KioskWallId is { } wallId && wallId != Guid.Empty)
        {
            return Redirect($"/walls/{wallId}?kiosk_blocked=1");
        }

        return Redirect("/walls?kiosk_blocked=1");
    }

    /// <summary>
    /// Reads and validates the link-intent cookie. Returns false (and leaves the login path untouched)
    /// when the cookie is absent, tampered, malformed or expired.
    /// </summary>
    private bool TryReadLinkIntent(out Guid userId, out string provider)
    {
        userId = Guid.Empty;
        provider = string.Empty;

        if (!Request.Cookies.TryGetValue(LinkIntentCookieName, out var raw) || string.IsNullOrEmpty(raw))
        {
            return false;
        }

        try
        {
            var decoded = _linkProtector.Unprotect(raw);
            var parts = decoded.Split('|');
            if (parts.Length != 3
                || !Guid.TryParseExact(parts[0], "N", out var parsedUser)
                || !long.TryParse(parts[2], out var expiresAtUnix))
            {
                return false;
            }

            if (DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix) < DateTimeOffset.UtcNow)
            {
                return false;
            }

            userId = parsedUser;
            provider = parts[1];
            return true;
        }
        catch (Exception)
        {
            // A bad/tampered cookie must never break login — fall through to the normal path.
            return false;
        }
    }

    private CookieOptions LinkIntentCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = LinkIntentTtl,
        Path = "/",
    };

    private CookieOptions LinkIntentCookieDeleteOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
    };
}
