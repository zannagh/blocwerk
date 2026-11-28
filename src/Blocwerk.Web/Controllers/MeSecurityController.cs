// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The owner's sign-in and security set-up for the companion app, read-only: e-mail and whether it is verified, password
/// sign-in, two-factor, linked providers. No secret is ever part of it (no hash, no TOTP secret).
/// </summary>
/// <remarks>
/// A linked device key only, rate limited like the other device reads, never cached. Every change (password, second
/// factor, recovery e-mail, linked logins, API keys, deleting the account) stays refused in a key session by
/// <c>ApiKeySessionGuard</c>, so a leaked key can never become a permanent takeover; the app opens
/// <see cref="ManagePath"/> in the browser for those, where the person signs in fully.
/// </remarks>
[ApiController]
[Route("api/v1/me/security")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeySyncRateLimit]
[Produces("application/json")]
public sealed class MeSecurityController : ControllerBase
{
    /// <summary>The web page where the security set-up is changed.</summary>
    public const string ManagePath = "/profile?tab=account";

    private readonly ICurrentUserService currentUser;

    public MeSecurityController(ICurrentUserService currentUser)
    {
        this.currentUser = currentUser;
    }

    /// <summary>The account's security set-up.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            var user = await currentUser.GetCurrentUserAsync();
            var providers = await currentUser.GetLinkedProvidersAsync();
            return Ok(new DeviceSecurityState(
                user.Email,
                user.EmailVerified,
                !string.IsNullOrEmpty(user.PasswordHash),
                user.LoginUsername,
                user.TotpEnabled,
                providers.Order(StringComparer.Ordinal).ToList(),
                ManagePath));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
