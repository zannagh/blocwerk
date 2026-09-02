using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>The caller's hangboard and pull-up work.</summary>
/// <remarks>
/// Antiforgery does not apply: the <c>[Authorize]</c> below pins the API-key scheme and nothing
/// else, so a browser cookie can never authorize these routes and there is no ambient credential to
/// forge with. <c>[IgnoreAntiforgeryToken]</c> states that in code rather than in a comment.
/// </remarks>
[ApiController]
[Route("api/v1/me/training")]
[Authorize(Policy = BlocwerkPolicies.UserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed class MeTrainingController : ControllerBase
{
    private const int MaxActivities = 100;

    private readonly ITrainingService trainingService;
    private readonly IProgressionService progressionService;

    public MeTrainingController(ITrainingService trainingService, IProgressionService progressionService)
    {
        this.trainingService = trainingService;
        this.progressionService = progressionService;
    }

    /// <summary>
    /// Recent training sessions, newest first. <see cref="ITrainingService"/> only writes, so the
    /// list is derived from the progression service's activities: the activities within the
    /// caller's progression window are walked newest-first and the ones that contain training are
    /// expanded into their sessions. <paramref name="activities"/> bounds how many activities are
    /// expanded, because each expansion is one query.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetRecent([FromQuery] int activities = 20)
    {
        var limit = Math.Clamp(activities, 1, MaxActivities);
        var summaries = await progressionService.GetActivitiesAsync();

        var hangboard = new List<HangboardSessionResponse>();
        var pullups = new List<PullupSessionResponse>();

        foreach (var summary in summaries
                     .Where(a => a.HangboardCount > 0 || a.PullupCount > 0)
                     .Take(limit))
        {
            var detail = await progressionService.GetActivityAsync(summary.Id);
            if (detail is null)
            {
                continue;
            }

            hangboard.AddRange(detail.Hangboard.Select(h => h.ToResponse()));
            pullups.AddRange(detail.Pullups.Select(p => p.ToResponse()));
        }

        return Ok(new TrainingResponse(
            hangboard.OrderByDescending(h => h.Timestamp).ToList(),
            pullups.OrderByDescending(p => p.Timestamp).ToList()));
    }

    [HttpPost("hangboard")]
    public async Task<IActionResult> SaveHangboard([FromBody] SaveHangboardRequest request)
    {
        if (request.DurationSeconds <= 0 || request.Sets <= 0 || request.EdgeSizeMm <= 0)
        {
            return BadRequest(new ApiErrorResponse("edgeSizeMm, durationSeconds and sets must be positive."));
        }

        var session = await trainingService.SaveHangboardSessionAsync(
            request.EdgeSizeMm,
            request.AdditionalWeightKg,
            TimeSpan.FromSeconds(request.DurationSeconds),
            request.Sets,
            request.Notes);

        return Ok(session.ToResponse());
    }

    [HttpPost("pullups")]
    public async Task<IActionResult> SavePullups([FromBody] SavePullupRequest request)
    {
        if (request.Repetitions <= 0 || request.Sets <= 0)
        {
            return BadRequest(new ApiErrorResponse("repetitions and sets must be positive."));
        }

        var session = await trainingService.SavePullupSessionAsync(
            request.Repetitions,
            request.AdditionalWeightKg,
            request.Sets,
            request.Notes);

        return Ok(session.ToResponse());
    }

    [HttpDelete("hangboard/{id:guid}")]
    public async Task<IActionResult> DeleteHangboard(Guid id)
    {
        try
        {
            await trainingService.DeleteHangboardSessionAsync(id);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Not your session."));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message));
        }
    }

    [HttpDelete("pullups/{id:guid}")]
    public async Task<IActionResult> DeletePullups(Guid id)
    {
        try
        {
            await trainingService.DeletePullupSessionAsync(id);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Not your session."));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message));
        }
    }
}
