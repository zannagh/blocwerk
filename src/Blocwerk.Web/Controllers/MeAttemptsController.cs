using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>The caller's boulder attempts.</summary>
[ApiController]
[Route("api/v1/me/attempts")]
[Authorize(Policy = BlocwerkPolicies.UserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class MeAttemptsController : ControllerBase
{
    /// <summary>Verbatim message the Core service uses for its membership guard.</summary>
    private const string NotAWallMember = "Only wall members can do this";

    private readonly IAttemptService attemptService;

    public MeAttemptsController(IAttemptService attemptService)
    {
        this.attemptService = attemptService;
    }

    /// <summary>All of the caller's attempts, optionally narrowed to one wall.</summary>
    [HttpGet]
    public async Task<IActionResult> GetMine([FromQuery] Guid? wallId)
    {
        var attempts = await attemptService.GetMyAttemptsAsync(wallId);
        return Ok(attempts.Select(a => a.ToResponse()).ToList());
    }

    /// <summary>
    /// Logs an attempt. Repeating a call with the same <c>clientRequestId</c> returns the stored
    /// attempt rather than creating a second one, so a retry after a timeout is safe.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Log([FromBody] LogAttemptApiRequest request)
    {
        if (request.BoulderId == Guid.Empty)
        {
            return BadRequest(new ApiErrorResponse("A boulderId is required."));
        }

        try
        {
            var attempt = await attemptService.LogAttemptAsync(
                request.BoulderId,
                request.Type,
                request.Notes,
                request.ClientRequestId,
                request.Timestamp);

            return Ok(attempt.ToResponse());
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Not allowed on this boulder."));
        }
        catch (InvalidOperationException ex) when (ex.Message == NotAWallMember)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex) when (ex.Message == AttemptService.DraftNotLoggableMessage)
        {
            // A draft is not climbable until it is published.
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // "Boulder not found".
            return NotFound(new ApiErrorResponse(ex.Message));
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            await attemptService.DeleteAttemptAsync(id);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Not your attempt."));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message));
        }
    }
}
