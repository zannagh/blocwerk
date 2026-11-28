// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Everyone's log on one boulder for the companion app, newest first: the web boulder page's History list, from the same
/// service calls (<see cref="IBoulderService.GetBoulderAsync"/> for the boulder,
/// <see cref="IAttemptService.GetAttemptsForBoulderAsync"/> for the rows), so it sees exactly what the owner's browser sees.
/// </summary>
/// <remarks>
/// A linked device key only, counted in the key's sync bucket, never cached. Who climbed what is for the wall's members,
/// as on the web: a wall the owner is not a member of, an unknown boulder, or a boulder of another wall all answer the
/// same bare 404, so the answer does not tell the boulder exists.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}/boulders/{boulderId:guid}/attempts")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeySyncRateLimit]
[Produces("application/json")]
public sealed class WallBoulderAttemptsController : ControllerBase
{
    private readonly IBoulderService boulders;
    private readonly IAttemptService attempts;
    private readonly ICurrentUserService currentUser;

    public WallBoulderAttemptsController(IBoulderService boulders, IAttemptService attempts, ICurrentUserService currentUser)
    {
        this.boulders = boulders;
        this.attempts = attempts;
        this.currentUser = currentUser;
    }

    /// <summary>The boulder's attempts, sends and flashes by every member, newest first, with the climber's name.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAttempts(Guid wallId, Guid boulderId)
    {
        try
        {
            var boulder = await boulders.GetBoulderAsync(boulderId);
            if (boulder is null || boulder.WallId != wallId)
            {
                return NotFound();
            }

            var me = await currentUser.GetCurrentUserAsync();
            var rows = await attempts.GetAttemptsForBoulderAsync(boulderId);
            return Ok(rows
                .Select(a => new BoulderAscentResponse(a.Id, a.UserId, a.User.Name, a.Type.ToString(), a.Timestamp, a.UserId == me.Id))
                .ToList());
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
