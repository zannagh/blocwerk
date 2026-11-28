// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Services;
using Blocwerk.Web.DeviceLinks;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The companion app's native Sign in with Apple: <c>POST /api/device-link/apple/nonce</c> issues a nonce, and
/// <c>POST /api/device-link/apple</c> trades an Apple identity token made with it for a device key, answering exactly
/// like the device link's code exchange.
/// </summary>
/// <remarks>
/// Anonymous: the Apple-signed token is the credential, and it works only for an account its Apple ID was linked to
/// from a signed-in app. Rate limited per client address; failures also count against a server-wide budget that a
/// success never touches (<see cref="AppleSignInFailureBudget"/>). JSON bodies only, so a cross-site form cannot post.
/// </remarks>
[ApiController]
[Route("api/device-link/apple")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class AppleSignInController : ControllerBase
{
    /// <summary>The 404 message when the Apple ID opens no account here.</summary>
    public const string NotLinkedMessage =
        "No Blocwerk account is linked to this Apple ID on this instance. Sign in once and link it under Profile.";

    private readonly IAppleSignInService apple;
    private readonly AppleSignInFailureBudget failures;

    public AppleSignInController(IAppleSignInService apple, AppleSignInFailureBudget failures)
    {
        this.apple = apple;
        this.failures = failures;
    }

    /// <summary>Issues a single-use nonce, alive for five minutes.</summary>
    [HttpPost("nonce")]
    [EnableRateLimiting(AppleSignInRateLimit.NoncePolicy)]
    public async Task<IActionResult> Nonce(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new AppleNonceResponse(await apple.CreateNonceAsync(ct)));
    }

    /// <summary>Signs the device in to the account linked to the token's Apple ID.</summary>
    [HttpPost]
    [EnableRateLimiting(AppleSignInRateLimit.SignInPolicy)]
    public async Task<IActionResult> SignIn([FromBody] AppleSignInRequest body, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        AppleSignInResult result;
        try
        {
            result = await apple.SignInAsync(body.IdentityToken, body.Nonce, body.Device, ct);
        }
        catch (Exception ex) when (ex is ApiKeySessionRestrictedException or KioskRestrictedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(ex.Message));
        }

        if (result.Key is { } key)
        {
            return Ok(new DeviceLinkTokenResponse(key.Token, key.KeyId, key.Name));
        }

        if (!failures.TryChargeFailure())
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        return result.Outcome == AppleSignInOutcome.NotLinked
            ? NotFound(new ApiErrorResponse(NotLinkedMessage))
            : BadRequest(new DeviceLinkTokenError("invalid_grant"));
    }
}
