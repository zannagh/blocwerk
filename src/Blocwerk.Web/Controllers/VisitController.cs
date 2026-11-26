// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Visits;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Starts a visit: the one place a visitor link is used. An anonymous, antiforgery-protected form post from the
/// link's landing page (<c>/visit/{token}</c>), answered with the visit cookie and a redirect to the wall.
/// </summary>
/// <remarks>
/// Every outcome other than a won redemption goes back to the landing page, which then tells the browser what the link
/// is: expired, used by another device, or (a double tap) already used by this one, which goes on to the wall. A
/// signed-in person never uses a link, and a kiosk tablet never reaches this route (<c>/visit</c> is not on its
/// allow-list), but both are refused here too.
/// </remarks>
public sealed class VisitController : Controller
{
    private readonly IWallVisitService visits;
    private readonly VisitGrantCookie visitCookie;
    private readonly IKioskContext kioskContext;

    public VisitController(IWallVisitService visits, VisitGrantCookie visitCookie, IKioskContext kioskContext)
    {
        this.visits = visits;
        this.visitCookie = visitCookie;
        this.kioskContext = kioskContext;
    }

    /// <summary>Uses the link and starts the visit.</summary>
    [HttpPost("/visit/{token}/start")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(string token, CancellationToken ct)
    {
        if (!VisitLinkTokens.IsWellFormed(token))
        {
            return NotFound();
        }

        await kioskContext.InitializeAsync();
        if (kioskContext.IsKiosk)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var landing = $"/visit/{Uri.EscapeDataString(token)}";
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(landing);
        }

        var grant = await visits.RedeemAsync(token, ct);
        if (grant is null)
        {
            return LocalRedirect(landing);
        }

        visitCookie.Write(HttpContext, grant);
        return LocalRedirect($"/walls/{grant.WallId}");
    }
}
