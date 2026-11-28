// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The caller's walls for the companion app: the list the web My Walls page shows, from the same service call
/// (<see cref="IWallService.GetMyWallsAsync"/>, the walls the owner is a member of) and the same card photo hashes.
/// </summary>
/// <remarks>
/// A linked device key only (<see cref="BlocwerkPolicies.DeviceApiKey"/>, on the API-key scheme): a plain personal key
/// or a browser cookie is refused. Rate limited like the feed. Never stored by a cache: the list is one person's.
/// </remarks>
[ApiController]
[Route("api/v1/me/walls")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
[Produces("application/json")]
public sealed class MeWallsController : ControllerBase
{
    private readonly IWallService walls;
    private readonly IAssetHashReader hashes;
    private readonly ICurrentUserService currentUser;

    public MeWallsController(IWallService walls, IAssetHashReader hashes, ICurrentUserService currentUser)
    {
        this.walls = walls;
        this.hashes = hashes;
        this.currentUser = currentUser;
    }

    /// <summary>The caller's walls in the web page's order: id, name, member count, the card photo's hash and the caller's role.</summary>
    [HttpGet]
    public async Task<IActionResult> GetWalls(CancellationToken ct)
    {
        if (!User.IsDeviceKey())
        {
            return NotFound();
        }

        List<Wall> mine;
        Guid userId;
        try
        {
            mine = await walls.GetMyWallsAsync();
            userId = (await currentUser.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }

        var photos = await hashes.WallPhotosAsync(mine.Select(w => w.Id).ToList(), ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(mine
            .Select(w => (Wall: w, Caller: WallCaller.Of(w, userId)))
            .Select(x => new MeWallResponse(
                x.Wall.Id,
                x.Wall.Name,
                x.Wall.Members.Count,
                x.Wall.PhotoContentType is not null,
                photos.GetValueOrDefault(x.Wall.Id),
                x.Caller.RoleName,
                x.Caller.IsOwner))
            .ToList());
    }
}
