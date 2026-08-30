using Blocwerk.Authentication.Resources;
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

        return Redirect(BuildProviderAuthorizeUrl(provider, state) ?? "/profile?link=unavailable");
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

        await db.UserIdentities.AddAsync(new UserIdentity
        {
            UserId = currentUser.Id,
            Provider = provider,
            ProviderUserId = providerUserId,
        });
        await db.SaveChangesAsync();
        return Redirect("/profile?link=linked");
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
