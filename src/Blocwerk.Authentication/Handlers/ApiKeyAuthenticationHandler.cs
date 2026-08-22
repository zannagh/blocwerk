using System.Security.Claims;
using System.Text.Encodings.Web;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blocwerk.Authentication.Handlers;

/// <summary>
/// Authenticates <c>Authorization: Bearer bwk_…</c> requests against the stored API keys.
/// </summary>
/// <remarks>
/// The produced principal deliberately carries the SAME name/name-identifier claims the key
/// owner's cookie or JWT principal carries, so that
/// <c>ClaimsHelper.ToUserIdentifier()</c> reproduces the stored
/// <see cref="User.Identifier"/> byte for byte. That makes every existing domain service work over
/// the API unchanged — and if it ever drifted, <c>CurrentUserService</c> would silently create a
/// second User row instead of failing loudly.
/// </remarks>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    /// <summary>The name this scheme is registered under.</summary>
    public const string SchemeName = "ApiKey";

    private const string BearerPrefix = "Bearer ";
    private const string IdentifierSeparator = "__";
    private const string FailureMessage = "Invalid API key.";

    private readonly IApiKeyService apiKeyService;
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyService apiKeyService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory)
        : base(options, logger, encoder)
    {
        this.apiKeyService = apiKeyService;
        this.dbContextFactory = dbContextFactory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[BearerPrefix.Length..].Trim();
        if (!token.StartsWith(ApiKey.TokenPrefix, StringComparison.Ordinal))
        {
            // A JWT; the policy scheme normally routes those elsewhere, but never claim them here.
            return AuthenticateResult.NoResult();
        }

        var key = await apiKeyService.ValidateAsync(token, Context.RequestAborted);
        if (key is null)
        {
            return AuthenticateResult.Fail(FailureMessage);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(Context.RequestAborted);
        db.CurrentUserId = Guid.Empty;
        var identifier = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == key.UserId)
            .Select(u => u.Identifier)
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (string.IsNullOrEmpty(identifier))
        {
            Logger.LogWarning("API key {ApiKeyId} references a user that no longer exists.", key.Id);
            return AuthenticateResult.Fail(FailureMessage);
        }

        var identity = BuildIdentity(identifier, key);
        if (identity is null)
        {
            Logger.LogWarning("API key {ApiKeyId} belongs to a user whose identifier is not claim-representable.", key.Id);
            return AuthenticateResult.Fail(FailureMessage);
        }

        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Machine callers get a plain 401 — never the cookie scheme's redirect to the login page.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilds the owner's identity claims from the stored identifier, which is
    /// <c>"{name}__{id}"</c>. Returns null when the identifier cannot be split that way, because a
    /// principal built from it would resolve to a different — and therefore new — user.
    /// </summary>
    private static ClaimsIdentity? BuildIdentity(string identifier, ApiKey key)
    {
        var separator = identifier.IndexOf(IdentifierSeparator, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var name = identifier[..separator];
        var userId = identifier[(separator + IdentifierSeparator.Length)..];
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name),
            new(ClaimTypes.NameIdentifier, userId),
            new("unique_name", name),
            new("nameid", userId),
            new(ApiKeyClaimTypes.Scope, key.Scope.ToString()),
            new(ApiKeyClaimTypes.ApiKeyId, key.Id.ToString()),
        };

        if (key.WallId is { } wallId)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.WallId, wallId.ToString()));
        }

        return new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
    }
}
