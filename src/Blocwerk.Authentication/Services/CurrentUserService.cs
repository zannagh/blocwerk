using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor? _accessor;
    private readonly AuthenticationStateProvider? _authenticationStateProvider;
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly BlocwerkSettings _settings;

    // Scoped service = one instance per circuit / HTTP request. The signed-in identity is stable for
    // that lifetime (sign-in/out does a full reload that starts a fresh scope), so resolve the User
    // once and reuse it. Without this a single page render fanned out to 5-6 identical Users lookups
    // (GetWall + GetSegments + GetBoulderList + GetActivity + GetHoldUsage each re-queried the user).
    private User? _cachedUser;

    public CurrentUserService(
        BlocwerkSettings settings,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        AuthenticationStateProvider? authenticationStateProvider = null,
        IHttpContextAccessor? accessor = null)
    {
        _accessor = accessor;
        _authenticationStateProvider = authenticationStateProvider;
        _dbContextFactory = dbContextFactory;
        _settings = settings;
    }

    public void InvalidateCache() => _cachedUser = null;

    public async Task<User> GetCurrentUserAsync()
    {
        if (_cachedUser is not null)
        {
            return _cachedUser;
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

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Identifier == identifier);
        if (user == null)
        {
            user = new User
            {
                Identifier = identifier,
                DisplayName = identifier.Split("__").FirstOrDefault() ?? identifier,
                Role = IdentityRole.User,
            };
            await dbContext.Users.AddAsync(user);
            await dbContext.SaveChangesAsync();
        }

        if (_settings.AdminIdentifiers.Contains(identifier) && user.Role != IdentityRole.Admin)
        {
            user.Role = IdentityRole.Admin;
            await dbContext.SaveChangesAsync();
        }

        _cachedUser = user;
        return user;
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
