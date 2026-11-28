// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The answer to the consent page (<c>/device-link</c>): Link makes a one-time code and redirects to the app with it,
/// Cancel tells the app the user said no. An antiforgery-protected form post of a signed-in user.
/// </summary>
/// <remarks>
/// The redirect target is the constant <see cref="DeviceLinkRequest.RedirectUri"/>, built by
/// <see cref="DeviceLinkCallback"/>, and only after the posted fields validate exactly as the page validated them, so
/// this route cannot be used as an open redirect. A kiosk tablet never reaches it (<c>/device-link</c> is not on its
/// allow-list).
/// </remarks>
[Authorize]
public sealed class DeviceLinkController : Controller
{
    private readonly IDeviceLinkService deviceLinks;
    private readonly ICurrentUserService currentUser;
    private readonly ILogger<DeviceLinkController> logger;

    public DeviceLinkController(
        IDeviceLinkService deviceLinks,
        ICurrentUserService currentUser,
        ILogger<DeviceLinkController> logger)
    {
        this.deviceLinks = deviceLinks;
        this.currentUser = currentUser;
        this.logger = logger;
    }

    /// <summary>Links the device, or declines, and sends the browser back to the app.</summary>
    [HttpPost("/device-link")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide([FromForm] DeviceLinkDecision form, CancellationToken ct)
    {
        var request = DeviceLinkRequest.TryParse(
            form.State, form.CodeChallenge, form.CodeChallengeMethod, form.Device, form.RedirectUri, out var reason);
        if (request is null)
        {
            logger.LogWarning("Device link post refused: {Reason}", reason);
            return BadRequest();
        }

        if (!string.Equals(form.Decision, "link", StringComparison.Ordinal))
        {
            return Redirect(DeviceLinkCallback.Denied(request.State));
        }

        try
        {
            var user = await currentUser.GetCurrentUserAsync();
            var code = await deviceLinks.CreateCodeAsync(user.Id, request, ct);
            return Redirect(DeviceLinkCallback.WithCode(code, request.State));
        }
        catch (ApiKeySessionRestrictedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }
}
