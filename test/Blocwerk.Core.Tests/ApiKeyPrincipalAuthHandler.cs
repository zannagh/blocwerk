// <copyright file="ApiKeyPrincipalAuthHandler.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Signs every request in as an API key the way the key handler builds it: a wall key for the wall in the
/// <see cref="WallHeader"/> header, otherwise a personal key with write access.
/// </summary>
internal sealed class ApiKeyPrincipalAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string WallHeader = "X-Test-Wall-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = Guid.TryParse(Request.Headers[WallHeader].ToString(), out var wallId)
            ? ApiKeys.Wall(wallId)
            : ApiKeys.Personal();
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
