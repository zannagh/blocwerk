// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// <c>POST /api/device-link/token</c>: the app exchanges its one-time code and PKCE verifier for a personal API key.
/// Anonymous (the code is the credential), rate limited per client address, and the key is in the answer only.
/// </summary>
/// <remarks>
/// Every refusal is the same 400 <c>invalid_grant</c>, so a caller learns nothing about why; the log says. No cookie
/// authorizes anything here (the code is the whole credential), and the body binds from JSON only, which a cross-site
/// form cannot send (a form post answers 415), so there is nothing for antiforgery to protect. There is no server-wide
/// budget on purpose: anyone could spend it and so block every device link, and a 256-bit code needs no such guard
/// against guessing.
/// </remarks>
[ApiController]
[Route("api/device-link")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class DeviceLinkTokenController : ControllerBase
{
    private readonly IDeviceLinkService deviceLinks;

    public DeviceLinkTokenController(IDeviceLinkService deviceLinks)
    {
        this.deviceLinks = deviceLinks;
    }

    /// <summary>Uses the code and answers with the new key, once.</summary>
    [HttpPost("token")]
    [EnableRateLimiting(DeviceLinkRateLimit.Policy)]
    public async Task<IActionResult> Exchange([FromBody] DeviceLinkTokenRequest body, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        DeviceLinkExchange? result;
        try
        {
            result = await deviceLinks.ExchangeAsync(body.Code, body.CodeVerifier, body.Device, ct);
        }
        catch (Exception ex) when (ex is ApiKeySessionRestrictedException or KioskRestrictedException)
        {
            // The key service refuses a kiosk tablet or an API-key session; that is a 403, not a server error.
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(ex.Message));
        }

        if (result is null)
        {
            return BadRequest(new DeviceLinkTokenError("invalid_grant"));
        }

        return Ok(new DeviceLinkTokenResponse(result.Token, result.KeyId, result.Name));
    }
}
