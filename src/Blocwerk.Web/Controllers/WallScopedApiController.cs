using Blocwerk.Authentication.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Base for the wall-scoped machine API. A wall API key carries the wall it was issued for as a
/// claim, and the authorization policy only proves the key <i>is</i> wall-scoped — not which wall
/// it belongs to. Every action therefore has to compare that claim against the wall in the route,
/// otherwise the key taped to the sensor on wall A would happily read and write wall B.
/// </summary>
public abstract class WallScopedApiController : ControllerBase
{
    /// <summary>
    /// Null when the calling key may act on <paramref name="wallId"/>, otherwise the 403 the
    /// action must return unchanged.
    /// </summary>
    protected IActionResult? GuardWall(Guid wallId)
    {
        if (User.GetApiKeyWallId() == wallId)
        {
            return null;
        }

        return StatusCode(
            StatusCodes.Status403Forbidden,
            new ApiErrorResponse("This API key is not valid for that wall."));
    }
}
