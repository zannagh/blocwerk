// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Core.Visits;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The web wall page's link buttons for the companion app, through the same service calls and so under the same rules:
/// Invite (any member gets the wall's invite link, made on first use), Generate Share Link (an admin replaces the invite,
/// which ends the old one; the public share link stays), Visit (a member makes a one-time visitor link, at most 20 an hour), and joining a wall from an
/// invite token. A wall the caller is not a member of answers 404 on the three wall routes.
/// </summary>
/// <remarks>
/// A linked device key only, never cached. The join preview takes the feed's rate limit, the POSTs the per-key write limit.
/// </remarks>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class WallLinksController : ControllerBase
{
    private const string NotYourWall = "You can't make links for this wall.";

    private const string InvalidInvite = "This invite link is not valid any more.";

    private readonly IWallService walls;
    private readonly IWallVisitService visits;
    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;
    private readonly BlocwerkSettings settings;

    public WallLinksController(
        IWallService walls,
        IWallVisitService visits,
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        BlocwerkSettings settings)
    {
        this.walls = walls;
        this.visits = visits;
        this.contexts = contexts;
        this.currentUser = currentUser;
        this.settings = settings;
    }

    /// <summary>The wall's invite link, made on first use (the web's Invite button). Any member.</summary>
    [HttpPost("walls/{wallId:guid}/invite-link")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> GetInvite(Guid wallId) =>
        InviteAsync(wallId, () => walls.GetOrCreateJoinTokenAsync(wallId));

    /// <summary>A new invite link in place of the old one, which stops working (the web's Generate Share Link). Admins.</summary>
    [HttpPost("walls/{wallId:guid}/invite-link/rotate")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> RotateInvite(Guid wallId) =>
        InviteAsync(wallId, () => walls.RotateJoinTokenAsync(wallId));

    /// <summary>A one-time visitor link (the web's Visit button). Members, at most 20 an hour.</summary>
    [HttpPost("walls/{wallId:guid}/visit-links")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> CreateVisit(Guid wallId, CancellationToken ct)
    {
        if (!await IsMemberAsync(wallId))
        {
            return NotFound();
        }

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

    /// <summary>The wall an invite token leads to, without joining it, so the app can ask "Join &lt;wall&gt;?" first.</summary>
    [HttpGet("join/{token}")]
    [EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
    public async Task<IActionResult> PreviewJoin(string token, [FromServices] IKioskContext kiosk, CancellationToken ct)
    {
        if (kiosk.IsKiosk)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Walls are joined from your own device."));
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.JoinToken != null && w.JoinToken == token)
            .Select(w => new JoinPreviewResponse(w.Id, w.Name, w.Members.Count))
            .FirstOrDefaultAsync(ct);
        return wall is null ? NotFound(new ApiErrorResponse(InvalidInvite)) : Ok(wall);
    }

    /// <summary>Joins the wall an invite token belongs to, as a member; joining again is a no-op.</summary>
    [HttpPost("join/{token}")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> Join(string token)
    {
        try
        {
            var wall = await walls.JoinWallAsync(token);
            return Ok(new JoinedWallResponse(wall.Id, wall.Name));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new ApiErrorResponse(InvalidInvite));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    private async Task<IActionResult> InviteAsync(Guid wallId, Func<Task<string>> mint)
    {
        if (!await IsMemberAsync(wallId))
        {
            return NotFound();
        }

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

    // A wall the caller is not on answers 404, as the member list does, so the answer does not tell the wall exists.
    private async Task<bool> IsMemberAsync(Guid wallId)
    {
        try
        {
            return await WallCaller.ReadAsync(contexts, currentUser, wallId, HttpContext.RequestAborted) is { IsMember: true };
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string LinkTo(string page, string token) =>
        $"{PublicBaseUrl.Of(settings, Request)}/{page}/{Uri.EscapeDataString(token)}";
}
