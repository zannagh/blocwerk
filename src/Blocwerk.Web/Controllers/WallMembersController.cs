// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// What the web wall page shows about a wall's people and panels, for the companion app: the member list (to members) and
/// the cross-panel link reminder's numbers (to admins and moderators). The data comes from the page's own service calls.
/// </summary>
/// <remarks>
/// A linked device key only, rate limited like the feed, never cached. A wall the caller is not a member of answers 404,
/// as an unknown one does; a member who may not edit holds gets 403 on the link numbers.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
[DeviceKeyOnly]
[Produces("application/json")]
public sealed class WallMembersController : ControllerBase
{
    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;
    private readonly IWallService walls;
    private readonly IWallPanelService panels;

    public WallMembersController(
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        IWallService walls,
        IWallPanelService panels)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
        this.walls = walls;
        this.panels = panels;
    }

    /// <summary>The wall's members in the order they joined, with their roles.</summary>
    [HttpGet("members")]
    public async Task<IActionResult> GetMembers(Guid wallId, CancellationToken ct)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true } caller)
        {
            return NotFound();
        }

        var members = await walls.GetMembersAsync(wallId);
        return Ok(members
            .Select(m => new WallMemberResponse(
                m.UserId,
                m.User?.Name ?? "Unknown",
                m.User is { } user ? MeProfileResponse.AvatarOf(user) : null,
                new WallCaller(m.UserId, caller.OwnerId, m.Role).RoleName,
                m.UserId == caller.OwnerId,
                m.JoinedAt))
            .ToList());
    }

    /// <summary>The live panels, the hold pairs linked across them and whether the links are finalized.</summary>
    [HttpGet("hold-links")]
    public async Task<IActionResult> GetHoldLinks(Guid wallId, CancellationToken ct)
    {
        var caller = await CallerAsync(wallId, ct);
        if (caller is not { IsMember: true })
        {
            return NotFound();
        }

        if (!caller.CanEditHolds)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Only wall admins and moderators link holds."));
        }

        var finalized = await LinksFinalizedAsync(wallId, ct);
        return Ok(await WallHoldLinkCount.CountAsync(panels, wallId, finalized));
    }

    private async Task<WallCaller?> CallerAsync(Guid wallId, CancellationToken ct)
    {
        try
        {
            return await WallCaller.ReadAsync(contexts, currentUser, wallId, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<bool> LinksFinalizedAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => w.LinksFinalizedGeneration == w.CurrentGeneration)
            .FirstOrDefaultAsync(ct);
    }
}
