// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The caller's own profile for the companion app: the identity card and the App preferences the web profile page keeps
/// on the server (display name, home wall, grade scale, notification opt-outs), written through the same
/// <see cref="ICurrentUserService"/> calls as the page.
/// </summary>
/// <remarks>
/// The page's other App settings (appearance, zoom lens, editor toolbar) live in a browser cookie, not on the account,
/// so they are not here. Avatar changes and account security stay on the web. A linked device key only, rate limited
/// like the feed, never cached.
/// </remarks>
[ApiController]
[Route("api/v1/me/profile")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class MeProfileController : ControllerBase
{
    private readonly ICurrentUserService currentUser;

    public MeProfileController(ICurrentUserService currentUser)
    {
        this.currentUser = currentUser;
    }

    /// <summary>The caller's identity and preferences.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            return Ok(MeProfileResponse.From(await currentUser.GetCurrentUserAsync()));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>Changes the fields sent (a merge patch, <see cref="ProfilePatch"/>) and answers the whole profile.</summary>
    [HttpPatch]
    public async Task<IActionResult> Patch([FromBody] JsonElement body)
    {
        var (patch, error) = ProfilePatch.Parse(body);
        if (patch is null)
        {
            return BadRequest(new ApiErrorResponse(error!));
        }

        try
        {
            if (patch.SetsHomeWall && await HomeWallRefusedAsync(patch.HomeWallId) is { } refused)
            {
                return refused;
            }

            await ApplyAsync(patch);
            currentUser.InvalidateCache();
            return Ok(MeProfileResponse.From(await currentUser.GetCurrentUserAsync()));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>Sets the home wall first, the one write that can be refused, so a refusal changes nothing.</summary>
    private async Task<IActionResult?> HomeWallRefusedAsync(Guid? wallId)
    {
        try
        {
            await currentUser.SetHomeWallAsync(wallId);
            return null;
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new ApiErrorResponse("homeWallId must be a wall you are a member of."));
        }
    }

    private async Task ApplyAsync(ProfilePatch patch)
    {
        if (patch.SetsDisplayName)
        {
            await currentUser.SetDisplayNameAsync(patch.DisplayName);
        }

        if (patch.PreferFontGrades is { } preferFont)
        {
            await currentUser.SetPreferFontGradesAsync(preferFont);
        }

        foreach (var (type, enabled) in patch.Notifications)
        {
            await currentUser.SetNotificationDisabledAsync(type, disabled: !enabled);
        }
    }
}
