using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Puts a wall into or out of "update mode" over the machine API. While enabled, everyone except the
/// admin who enabled it sees a "currently being updated" notice instead of the wall. Authentication is
/// API key only (scheme pinned so a browser cookie can never reach this route), and the key's owner
/// must be an admin of the wall — enforced inside the service.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/maintenance")]
[Authorize(Policy = BlocwerkPolicies.WallApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallMaintenanceController : WallScopedApiController
{
    private readonly IWallService wallService;

    public WallMaintenanceController(IWallService wallService)
    {
        this.wallService = wallService;
    }

    /// <summary>Enables or disables update mode for the wall named in the route.</summary>
    [HttpPut]
    [Consumes("application/json")]
    public async Task<IActionResult> SetMaintenance(Guid wallId, [FromBody] WallMaintenanceRequest body)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await wallService.SetMaintenanceAsync(wallId, body.Enabled);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new ApiErrorResponse("This API key's owner is not an admin of that wall."));
        }
    }
}
