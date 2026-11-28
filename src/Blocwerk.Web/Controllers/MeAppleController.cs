// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.AppleSignIn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The caller's linked Apple ID: <c>POST /api/v1/me/apple</c> links the Apple ID of a fresh identity token (made with a
/// nonce from <c>POST /api/device-link/apple/nonce</c>), <c>DELETE</c> unlinks it.
/// </summary>
/// <remarks>
/// A linked device key only, so linking happens from the app while it is signed in. One Apple ID opens one account:
/// an Apple ID already linked to another account answers 409. Linking another Apple ID replaces the account's old one.
/// </remarks>
[ApiController]
[Route("api/v1/me/apple")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class MeAppleController : ControllerBase
{
    /// <summary>The 409 message when the Apple ID belongs to another account.</summary>
    public const string LinkedElsewhereMessage =
        "This Apple ID is already linked to another Blocwerk account on this instance. Unlink it there first.";

    /// <summary>The 400 message when the token or nonce does not verify.</summary>
    public const string InvalidTokenMessage =
        "The Apple identity token or nonce is not valid. Fetch a new nonce and sign in with Apple again.";

    private readonly ICurrentUserService currentUser;
    private readonly IAppleSignInService apple;

    public MeAppleController(ICurrentUserService currentUser, IAppleSignInService apple)
    {
        this.currentUser = currentUser;
        this.apple = apple;
    }

    /// <summary>Links the token's Apple ID to the caller's account.</summary>
    [HttpPost]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> Link([FromBody] AppleLinkRequest body, CancellationToken ct)
    {
        try
        {
            var user = await currentUser.GetCurrentUserAsync();
            return await apple.LinkAsync(user.Id, body.IdentityToken, body.Nonce, ct) switch
            {
                AppleLinkOutcome.Linked => NoContent(),
                AppleLinkOutcome.LinkedElsewhere => Conflict(new ApiErrorResponse(LinkedElsewhereMessage)),
                _ => BadRequest(new ApiErrorResponse(InvalidTokenMessage)),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>Unlinks the caller's Apple ID; unlinking none is a no-op.</summary>
    [HttpDelete]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        try
        {
            var user = await currentUser.GetCurrentUserAsync();
            await apple.UnlinkAsync(user.Id, ct);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
