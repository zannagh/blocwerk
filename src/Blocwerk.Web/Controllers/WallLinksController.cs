// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Blocwerk.Core.Visits;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The web wall page's link buttons for the companion app, through the same service calls and so under the same rules:
/// Invite (any member gets the wall's invite link, made on first use), Generate Share Link (an admin replaces it, which
/// ends the old one), Visit (a member makes a one-time visitor link, at most 20 an hour), and joining a wall from an
/// invite token.
/// </summary>
/// <remarks>A linked device key only, on the per-key write limit, never cached.</remarks>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeyWriteRateLimit]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class WallLinksController : ControllerBase
{
    private const string NotYourWall = "You can't make links for this wall.";

    private readonly IWallService walls;
    private readonly IWallVisitService visits;

    public WallLinksController(IWallService walls, IWallVisitService visits)
    {
        this.walls = walls;
        this.visits = visits;
    }

    /// <summary>The wall's invite link, made on first use (the web's Invite button). Any member.</summary>
    [HttpPost("walls/{wallId:guid}/invite-link")]
    public Task<IActionResult> GetInvite(Guid wallId) =>
        InviteAsync(() => walls.GetOrCreateShareTokenAsync(wallId));

    /// <summary>A new invite link in place of the old one, which stops working (the web's Generate Share Link). Admins.</summary>
    [HttpPost("walls/{wallId:guid}/invite-link/rotate")]
    public Task<IActionResult> RotateInvite(Guid wallId) =>
        InviteAsync(() => walls.GenerateShareTokenAsync(wallId));

    /// <summary>A one-time visitor link (the web's Visit button). Members, at most 20 an hour.</summary>
    [HttpPost("walls/{wallId:guid}/visit-links")]
    public async Task<IActionResult> CreateVisit(Guid wallId, CancellationToken ct)
    {
        try
        {
            var created = await visits.CreateAsync(wallId, ct);
            return Ok(new WallLinkResponse(created.Token, LinkTo("visit", created.Token), created.ExpiresAt));
        }
        catch (VisitLinkLimitException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiErrorResponse(ex.Message));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(NotYourWall));
        }
    }

    /// <summary>Joins the wall an invite token belongs to, as a member; joining again is a no-op.</summary>
    [HttpPost("join/{token}")]
    public async Task<IActionResult> Join(string token)
    {
        try
        {
            var wall = await walls.JoinWallAsync(token);
            return Ok(new JoinedWallResponse(wall.Id, wall.Name));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new ApiErrorResponse("This invite link is not valid any more."));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    private async Task<IActionResult> InviteAsync(Func<Task<string>> mint)
    {
        try
        {
            var token = await mint();
            return Ok(new WallLinkResponse(token, LinkTo("join", token), null));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(NotYourWall));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    private string LinkTo(string page, string token) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/{page}/{Uri.EscapeDataString(token)}";
}
