// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Another member's profile for the companion app, as the web's <c>/profile/{id}</c> shows it: the identity card and
/// the progression pane, only to someone who shares a wall with them (or to themselves). Anyone else gets the same 404
/// as an unknown id, as the page shows "Profile unavailable" for both.
/// </summary>
/// <remarks>A linked device key only, rate limited like the feed, never cached.</remarks>
[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
[DeviceKeyOnly]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    /// <summary>The window the web's other-member view opens on.</summary>
    public const int DefaultWindowDays = 90;

    private readonly ICurrentUserService currentUser;
    private readonly IWallService walls;
    private readonly IProgressionService progression;

    public UsersController(ICurrentUserService currentUser, IWallService walls, IProgressionService progression)
    {
        this.currentUser = currentUser;
        this.walls = walls;
        this.progression = progression;
    }

    /// <summary>The member's public profile, with their progression over <paramref name="windowDays"/> (7 to 365).</summary>
    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(
        Guid userId,
        [FromQuery] int windowDays = DefaultWindowDays,
        [FromQuery] ProgressionGroupBy groupBy = ProgressionGroupBy.Week)
    {
        if (!Enum.IsDefined(groupBy))
        {
            return BadRequest(new ApiErrorResponse("groupBy must be Day, Week or Month."));
        }

        try
        {
            var viewer = await currentUser.GetCurrentUserAsync();
            var target = await currentUser.GetUserByIdAsync(userId);
            if (target is null || target.IsDeleted
                || (viewer.Id != userId && !await walls.UsersShareAWallAsync(viewer.Id, userId)))
            {
                return NotFound();
            }

            var shown = await progression.GetProgressionForUserAsync(userId, windowDays, groupBy);
            return Ok(new UserProfileResponse(
                target.Id,
                target.Name,
                target.Role.ToString(),
                target.CreatedAt,
                MeProfileResponse.AvatarOf(target),
                shown.ToResponse()));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
