// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;
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
/// authorizes anything here, so there is no ambient credential to forge with and antiforgery does not apply.
/// </remarks>
[ApiController]
[Route("api/device-link")]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
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
        var result = await deviceLinks.ExchangeAsync(body.Code, body.CodeVerifier, body.Device, ct);
        if (result is null)
        {
            return BadRequest(new DeviceLinkTokenError("invalid_grant"));
        }

        return Ok(new DeviceLinkTokenResponse(result.Token, result.KeyId, result.Name));
    }
}
