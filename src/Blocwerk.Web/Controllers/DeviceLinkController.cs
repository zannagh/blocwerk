// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Services;
using Blocwerk.Web.DeviceLinks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The answer to the consent page (<c>/device-link</c>, posted to <c>/device-link/confirm</c> because the page itself
/// owns POST on its own route): Link makes a one-time code and redirects to the app with it, Cancel tells the app the
/// user said no. An antiforgery-protected form post of a signed-in user.
/// </summary>
/// <remarks>
/// The redirect target is the constant <see cref="DeviceLinkRequest.RedirectUri"/>, built by
/// <see cref="DeviceLinkCallback"/>, and only after the posted fields validate exactly as the page validated them, so
/// this route cannot be used as an open redirect. Link also needs the page's own ticket (<see cref="DeviceLinkPageTicket"/>):
/// a page older than five minutes is shown again instead, with its match code. Each user may post ten times a minute.
/// </remarks>
[Authorize]
public sealed class DeviceLinkController : Controller
{
    private readonly IDeviceLinkService deviceLinks;
    private readonly ICurrentUserService currentUser;
    private readonly DeviceLinkPageTicket tickets;
    private readonly DeviceLinkLimits limits;
    private readonly ILogger<DeviceLinkController> logger;

    public DeviceLinkController(
        IDeviceLinkService deviceLinks,
        ICurrentUserService currentUser,
        DeviceLinkPageTicket tickets,
        DeviceLinkLimits limits,
        ILogger<DeviceLinkController> logger)
    {
        this.deviceLinks = deviceLinks;
        this.currentUser = currentUser;
        this.tickets = tickets;
        this.limits = limits;
        this.logger = logger;
    }

    /// <summary>Links the device, or declines, and sends the browser back to the app.</summary>
    [HttpPost("/device-link/confirm")]
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

        var user = await currentUser.GetCurrentUserAsync();
        if (!limits.TryConfirm(user.Id))
        {
            logger.LogWarning("Device link post rate limit hit by user {UserId}", user.Id);
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!string.Equals(form.Decision, "link", StringComparison.Ordinal))
        {
            return Redirect(DeviceLinkCallback.Denied(request.State));
        }

        if (!tickets.IsValid(form.Ticket, user.Id, request.CodeChallenge))
        {
            return LocalRedirect(PageAgain(request));
        }

        return await LinkAsync(user.Id, request, ct);
    }

    private async Task<IActionResult> LinkAsync(Guid userId, DeviceLinkRequest request, CancellationToken ct)
    {
        try
        {
            var code = await deviceLinks.CreateCodeAsync(userId, request, ct);
            return Redirect(DeviceLinkCallback.WithCode(code, request.State));
        }
        catch (DeviceLinkLimitException)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }
        catch (Exception ex) when (ex is ApiKeySessionRestrictedException or KioskRestrictedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>The same consent page again, marked as reopened because the first one was too old.</summary>
    private static string PageAgain(DeviceLinkRequest request) =>
        "/device-link"
        + $"?state={Uri.EscapeDataString(request.State)}"
        + $"&code_challenge={Uri.EscapeDataString(request.CodeChallenge)}"
        + $"&code_challenge_method={DeviceLinkRequest.ChallengeMethod}"
        + $"&device={Uri.EscapeDataString(request.DeviceName)}"
        + $"&redirect_uri={Uri.EscapeDataString(DeviceLinkRequest.RedirectUri)}"
        + "&reopened=1";
}
