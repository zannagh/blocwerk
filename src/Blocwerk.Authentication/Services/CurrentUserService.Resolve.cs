using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The identity half of <see cref="CurrentUserService"/>: turning the claims on a request or a
/// circuit into the one <see cref="User"/> row that session is allowed to act as — and refusing
/// when they name nobody who may.
/// </summary>
public partial class CurrentUserService
{
    public async Task<User> GetCurrentUserAsync()
    {
        if (_cachedUser is not null)
        {
            return _cachedUser;
        }

        // A refusal is as final for this scope as a success is. The claims cannot start naming
        // somebody who may sign in before the next request/circuit, and a single render fans out to
        // five or six resolutions — each of which would otherwise re-query, re-refuse and append
        // another deletion Set-Cookie to the same response.
        if (_resolutionRefused)
        {
            throw new UnauthorizedAccessException();
        }

        var claimsIdentity = await TryGetClaimsIdentityFromCookie()
                             ?? TryGetClaimsIdentityFromHttpContext();

        if (claimsIdentity == null)
        {
            throw new UnauthorizedAccessException();
        }

        string identifier = claimsIdentity.ToUserIdentifier();

        if (string.IsNullOrEmpty(identifier))
        {
            throw new UnauthorizedAccessException();
        }

        // The provider ("github"/"google"/"microsoft") is present for OAuth logins and absent for
        // legacy cookies and dev login. providerUserId is the stable provider subject (nameid).
        string provider = claimsIdentity.GetProvider();
        string providerUserId = claimsIdentity.ToUserId();

        // Password (and TOTP) sign-ins stamp the exact user id as a "uid" claim. OAuth logins carry none.
        string uid = claimsIdentity.FindFirst("uid")?.Value ?? string.Empty;

        // Whether this session authenticated just now. Only a fresh login may CREATE an account.
        bool freshLogin = AuthFreshness.IsFresh(claimsIdentity);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        User user;
        try
        {
            user = await ResolveUserAsync(dbContext, identifier, provider, providerUserId, uid, freshLogin);
        }
        catch (UnauthorizedAccessException)
        {
            // The claims are valid but they name nobody who may sign in — a deletion tombstone, a
            // system row, or a stale cookie for an account that no longer exists. Drop the cookie so
            // the browser stops re-presenting it, then refuse as usual. Remembered for this scope so
            // the drop happens once rather than once per resolution.
            _resolutionRefused = true;
            await TrySignOutAsync();
            throw;
        }

        // AdminIdentifiers is authoritative for the app-wide admin bit in BOTH directions: on every
        // resolution Role == Admin IFF the identifier is configured. Promote a configured admin that
        // isn't yet Admin, and — critically — demote an identifier that is Admin but no longer
        // configured, so removing someone from AdminIdentifiers actually revokes their admin. Only the
        // Admin/User toggle is touched here (Guest and other role semantics are left alone), and the
        // branches are mutually exclusive so a single resolution never promotes and demotes.
        bool shouldBeAdmin = _settings.AdminIdentifiers.Contains(identifier);
        if (shouldBeAdmin && user.Role != IdentityRole.Admin)
        {
            user.Role = IdentityRole.Admin;
            await dbContext.SaveChangesAsync();
        }
        else if (!shouldBeAdmin && user.Role == IdentityRole.Admin)
        {
            user.Role = IdentityRole.User;
            await dbContext.SaveChangesAsync();
        }

        _cachedUser = user;
        return user;
    }

    /// <summary>
    /// Resolves the current <see cref="User"/> from the login claims, attaching a
    /// <see cref="UserIdentity"/> lazily. This is the ONLY user-creation path: a user is only ever
    /// born from an OAuth login. Resolution order:
    /// (0) a "uid" claim (stamped by password/TOTP sign-in) → that exact user by id, never creating one.
    ///     This makes password sessions resolve precisely and sidesteps the lossy "{name}__{id}"
    ///     identifier round-trip, which misresolves (and would otherwise CREATE a blank user) when the
    ///     name itself contains "__";
    /// (a)/(b) for an OAuth login, resolve through the shared <see cref="LegacyIdentityResolver"/>
    ///     (UserIdentity row → legacy Identifier subject-suffix), back-filling a
    ///     <see cref="UserIdentity"/> when the match came from a legacy row. Sharing this resolver with
    ///     the account-link path is what keeps login and linking from diverging on identity ownership;
    /// (b') no provider claim (dev login / legacy cookie) → resolve purely by the full identifier;
    /// (c) no user at all → create the user (as before) plus, for an OAuth login, its identity.
    /// </summary>
    private static async Task<User> ResolveUserAsync(
        BlocwerkDbContext dbContext,
        string identifier,
        string provider,
        string providerUserId,
        string uid,
        bool freshLogin)
    {
        // (0) Exact resolution by uid claim for password/TOTP sessions — first, and never creates a user.
        if (!string.IsNullOrEmpty(uid) && Guid.TryParse(uid, out var uidGuid))
        {
            // A uid claim names exactly ONE account, so this branch is terminal either way: if that
            // account is gone, erased or a system row, the session is over. Falling through would let
            // a cookie for a deleted account re-resolve by its (now rewritten) identifier or, worse,
            // create a fresh account from it.
            return EnsureLive(await dbContext.Users.FirstOrDefaultAsync(u => u.Id == uidGuid));
        }

        bool hasProvider = !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(providerUserId);

        // (a)/(b) Resolve the OAuth identity through the ONE shared resolver, then back-fill a
        // UserIdentity so a legacy match becomes a first-class identity row (no-op if it already exists).
        if (hasProvider)
        {
            var owner = await LegacyIdentityResolver.FindByProviderIdentityAsync(dbContext, provider, providerUserId);
            if (owner is not null)
            {
                EnsureLive(owner);
                await EnsureIdentityAsync(dbContext, owner.Id, provider, providerUserId);
                return owner;
            }
        }

        // (b') No provider claim (dev login / legacy cookie): resolve purely by the full identifier.
        // (An OAuth login that reaches here found no identity and no legacy subject match, so the
        // full-identifier lookup below would not match it either — it falls through to creation.)
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Identifier == identifier);
        if (user is not null)
        {
            return EnsureLive(user);
        }

        // (c) No user exists yet. Creation is gated on the login having JUST happened, because this is
        // precisely where a still-valid cookie for a DELETED account lands: erasure drops its provider
        // identities and rewrites its identifier, so nothing above can match it any more. Without this
        // gate an 8-hour-old (or stolen) cookie would silently mint a brand-new account with no fresh
        // consent from the provider behind it. See AuthFreshness.
        if (!freshLogin)
        {
            throw new UnauthorizedAccessException();
        }

        user = new User
        {
            Identifier = identifier,
            DisplayName = identifier.Split("__").FirstOrDefault() ?? identifier,
            Role = IdentityRole.User,
        };
        await dbContext.Users.AddAsync(user);
        await dbContext.SaveChangesAsync();

        if (hasProvider)
        {
            await EnsureIdentityAsync(dbContext, user.Id, provider, providerUserId);
        }

        return user;
    }

    /// <summary>
    /// Asserts that a resolved row is a LIVE person's account, and turns anything else into "not
    /// authenticated".
    /// </summary>
    /// <remarks>
    /// Two rows exist in <c>Users</c> that no session may ever act as. A deletion tombstone is the
    /// scrubbed remains of somebody who left: resolving it would let any still-open tab (or a stolen
    /// cookie) set a new e-mail, name and avatar on the erased row and re-personalise it. The seeded
    /// Ghost row is nobody at all, and owns every anonymously-set boulder in the installation.
    /// Refusing both HERE, at the single point every branch funnels through, is what keeps the check
    /// from being forgotten on the next branch somebody adds.
    /// </remarks>
    private static User EnsureLive(User? user)
    {
        if (user is null || user.IsDeleted || GhostUser.Is(user.Id))
        {
            throw new UnauthorizedAccessException();
        }

        return user;
    }

    /// <summary>
    /// Best-effort cookie drop for a session whose claims no longer name anybody who may sign in.
    /// </summary>
    /// <remarks>
    /// The refusal itself is the <see cref="UnauthorizedAccessException"/> the caller re-throws; this
    /// only stops the browser from re-presenting a cookie that can never resolve again. A Blazor
    /// circuit has no <see cref="HttpContext"/> to sign out of, and a response already on the wire
    /// cannot take a Set-Cookie, so both cases simply do nothing.
    /// </remarks>
    private async Task TrySignOutAsync()
    {
        if (_accessor?.HttpContext is not { } httpContext || httpContext.Response.HasStarted)
        {
            return;
        }

        try
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
        catch (Exception)
        {
            // Never let the sign-out attempt replace the authorization failure with a different error.
        }
    }

    private static async Task EnsureIdentityAsync(BlocwerkDbContext dbContext, Guid userId, string provider, string providerUserId)
    {
        bool exists = await dbContext.UserIdentities
            .AnyAsync(i => i.Provider == provider && i.ProviderUserId == providerUserId);
        if (exists)
        {
            return;
        }

        await dbContext.UserIdentities.AddAsync(new UserIdentity
        {
            UserId = userId,
            Provider = provider,
            ProviderUserId = providerUserId,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task<ClaimsIdentity?> TryGetClaimsIdentityFromCookie()
    {
        if (_authenticationStateProvider == null)
        {
            return null;
        }

        var cookieState = await _authenticationStateProvider.GetAuthenticationStateAsync();

        if (cookieState.User is not { Identity.IsAuthenticated: true }
            || cookieState.User.FindFirst(ClaimTypes.NameIdentifier) is not { } nameIdentifier
            || cookieState.User.FindFirst(ClaimTypes.Name) is not { } name)
        {
            return null;
        }

        var cookieClaim = new ClaimsIdentity();
        cookieClaim.AddClaim(new Claim(ClaimTypes.NameIdentifier, nameIdentifier.Value));
        cookieClaim.AddClaim(new Claim(ClaimTypes.Name, name.Value));

        // Carry the "uid" claim through when present (password/TOTP sign-ins) so resolution can look the
        // user up by their exact id — this is what keeps a password session from misresolving (and
        // creating a blank user) when the display name contains "__".
        if (cookieState.User.FindFirst("uid") is { } uidClaim && !string.IsNullOrEmpty(uidClaim.Value))
        {
            cookieClaim.AddClaim(new Claim("uid", uidClaim.Value));
        }

        // Carry the sign-in instant through so resolution can tell a login that just happened from a
        // cookie minted hours ago — the difference between an OAuth signup and a stale cookie for an
        // account that has since been deleted.
        if (cookieState.User.FindFirst(AuthFreshness.ClaimType) is { } authTimeClaim
            && !string.IsNullOrEmpty(authTimeClaim.Value))
        {
            cookieClaim.AddClaim(new Claim(AuthFreshness.ClaimType, authTimeClaim.Value));
        }

        // Carry the provider claim through when present (OAuth logins) so resolution can look the user
        // up by their provider identity. Absent for legacy cookies signed before this change and for
        // dev login, which then fall back to identifier-based resolution.
        if (cookieState.User.FindFirst("provider") is { } providerClaim
            && !string.IsNullOrEmpty(providerClaim.Value))
        {
            cookieClaim.AddClaim(new Claim("provider", providerClaim.Value));
        }

        return cookieClaim;
    }

    private ClaimsIdentity? TryGetClaimsIdentityFromHttpContext()
    {
        if (_accessor?.HttpContext is not { } httpContext)
        {
            return null;
        }

        // An API key resolves to its OWNER, which then opens every wall that owner belongs to via
        // the membership query filter. That is only ever acceptable on an endpoint that explicitly
        // opted into authorization — those compare the route's wall against the key's own wall
        // claim (WallScopedApiController.GuardWall) or are scoped to the owner by definition
        // (/api/v1/me/*). An unguarded endpoint that merely happens to sit under /api/walls did no
        // such check, so the key must not resolve a user there at all.
        if (httpContext.User.IsApiKeyPrincipal() && !ApiKeySurface.HasExplicitAuthorization(httpContext))
        {
            return null;
        }

        if (httpContext.User.Identity is ClaimsIdentity { IsAuthenticated: true } httpClaimsIdentity)
        {
            return httpClaimsIdentity;
        }

        return null;
    }
}
