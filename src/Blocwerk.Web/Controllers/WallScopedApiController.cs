using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Enums;
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

    /// <summary>
    /// The guard of the wall-admin machine API: admits a PERSONAL key (User scope, no wall claim) or a WALL key
    /// bound to <paramref name="wallId"/>, and either only when its owner created it with write access
    /// (<c>ApiKey.AllowWrite</c>). Only for actions whose service decides per wall from the acting user — wall
    /// admin, not a kiosk — so the key meets exactly the checks its owner meets in the browser. A wall key without
    /// write access keeps its fixed device surface (temperature, images, maintenance) and nothing more.
    /// </summary>
    protected IActionResult? GuardWallOrPersonalKey(Guid wallId)
    {
        var scope = User.GetApiKeyScope();
        if (scope == ApiKeyScope.User)
        {
            return User.IsWritablePersonalKey() ? null : NoWriteAccess();
        }

        // Kiosk and installation keys never reach the wall-admin API, whatever wall they carry.
        if (scope != ApiKeyScope.Wall)
        {
            return NoWriteAccess();
        }

        if (GuardWall(wallId) is { } wrongWall)
        {
            return wrongWall;
        }

        return User.IsWritableWallKey(wallId) ? null : NoWriteAccess();
    }

    private ObjectResult NoWriteAccess()
    {
        return StatusCode(
            StatusCodes.Status403Forbidden,
            new ApiErrorResponse("This API key may not change walls. Create a key with write access."));
    }
}
