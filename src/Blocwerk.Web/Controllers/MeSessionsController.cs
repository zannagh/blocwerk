using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The caller's climbing sessions. The domain services resolve "the caller" from the principal,
/// and an API-key principal carries the same identity claims as its owner's cookie principal, so
/// nothing here has to pass a user id.
/// </summary>
[ApiController]
[Route("api/v1/me/sessions")]
[Authorize(Policy = BlocwerkPolicies.UserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class MeSessionsController : ControllerBase
{
    private readonly ISessionService sessionService;

    public MeSessionsController(ISessionService sessionService)
    {
        this.sessionService = sessionService;
    }

    /// <summary>The live session, or 404 when none is open.</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActive()
    {
        var session = await sessionService.GetActiveSessionAsync();
        if (session is null)
        {
            return NotFound(new ApiErrorResponse("No session is currently open."));
        }

        return Ok(session.ToResponse());
    }

    /// <summary>Starts a session on a wall, ending any session that was still open.</summary>
    [HttpPost]
    public async Task<IActionResult> Start([FromBody] StartSessionRequest request)
    {
        if (request.WallId == Guid.Empty)
        {
            return BadRequest(new ApiErrorResponse("A wallId is required."));
        }

        try
        {
            var session = await sessionService.StartSessionAsync(request.WallId);
            return Ok(session.ToResponse());
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Not allowed on this wall."));
        }
        catch (InvalidOperationException ex)
        {
            // "Wall not found" — also raised for a wall the caller cannot see, by design.
            return NotFound(new ApiErrorResponse(ex.Message));
        }
    }

    /// <summary>Ends the live session. Idempotent: ending nothing is still a success.</summary>
    [HttpPost("end")]
    public async Task<IActionResult> End()
    {
        await sessionService.EndSessionAsync();
        return NoContent();
    }
}
