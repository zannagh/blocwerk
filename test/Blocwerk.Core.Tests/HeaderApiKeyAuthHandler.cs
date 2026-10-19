// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A test stand-in for the API-key scheme: builds the principal the request names in its <see cref="Header"/>
/// ("personal-write-key", "read-only-personal-key", "wall-key", "kiosk-key"); no header is anonymous.
/// </summary>
internal sealed class HeaderApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Header = "X-Who";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var who = Request.Headers[Header].ToString();
        var claims = new List<Claim> { new(ClaimTypes.Name, "Someone"), new(ClaimTypes.NameIdentifier, "gh|1") };
        switch (who)
        {
            case "personal-write-key":
                claims.AddRange(Key(ApiKeyScope.User, write: true));
                break;
            case "read-only-personal-key":
                claims.AddRange(Key(ApiKeyScope.User, write: false));
                break;
            case "wall-key":
                claims.AddRange(Key(ApiKeyScope.Wall, write: true, Guid.NewGuid()));
                break;
            case "kiosk-key":
                claims.AddRange(Key(ApiKeyScope.Kiosk, write: true, Guid.NewGuid()));
                break;
            default:
                return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyAuthenticationHandler.SchemeName)));
    }

    private static IEnumerable<Claim> Key(ApiKeyScope scope, bool write, Guid? wall = null)
    {
        yield return new Claim(ApiKeyClaimTypes.Scope, scope.ToString());
        yield return new Claim(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString());
        if (write)
        {
            yield return new Claim(ApiKeyClaimTypes.AllowWrite, "true");
        }

        if (wall is { } id)
        {
            yield return new Claim(ApiKeyClaimTypes.WallId, id.ToString());
        }
    }
}
