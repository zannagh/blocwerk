// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Membership;
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
/// A linked device key only, never cached; the reads take the feed's rate limit, the removal and the leave the per-key
/// write limit. A wall the caller is not a member of answers 404, as an unknown one does; a member who may not edit holds
/// gets 403 on the link numbers.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class WallMembersController : ControllerBase
{
    private const string MembershipChanged = "This membership changed at the same moment. Reload the members and try again.";

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
    [EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
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
    [EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
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

    /// <summary>Removes a member, as the members list's Remove does. Wall admins; never the owner, never oneself.</summary>
    [HttpDelete("members/{userId:guid}")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> RemoveMember(
        Guid wallId,
        Guid userId,
        [FromServices] IWallMembershipService membership,
        CancellationToken ct)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true })
        {
            return NotFound();
        }

        return await EndAsync(() => membership.RemoveMemberAsync(wallId, userId, ct));
    }

    /// <summary>
    /// The caller leaves the wall, as the members list's Leave does. Any member but the owner. The beta clips they
    /// uploaded to the wall go too unless <paramref name="deleteBetaVideos"/> is false.
    /// </summary>
    [HttpPost("leave")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> Leave(
        Guid wallId,
        [FromServices] IWallMembershipService membership,
        CancellationToken ct,
        [FromQuery] bool deleteBetaVideos = true)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true })
        {
            return NotFound();
        }

        return await EndAsync(() => membership.LeaveAsync(wallId, deleteBetaVideos, ct));
    }

    private async Task<IActionResult> EndAsync(Func<Task> end)
    {
        try
        {
            await end();
            return NoContent();
        }
        catch (MembershipRefusedException ex) when (ex.Reason == MembershipRefusal.NotAMember)
        {
            return NotFound(new ApiErrorResponse(ex.Message));
        }
        catch (MembershipRefusedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(ex.Message));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Only wall admins remove members."));
        }
        catch (DbUpdateConcurrencyException)
        {
            // The membership changed under this request (a role change, another removal): nothing was saved.
            return Conflict(new ApiErrorResponse(MembershipChanged));
        }
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
