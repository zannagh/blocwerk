using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>The caller's progression scores, activities and activity grid.</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = BlocwerkPolicies.UserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class MeProgressionController : ControllerBase
{
    private const int MaxWeeks = 260;

    private readonly IProgressionService progressionService;

    public MeProgressionController(IProgressionService progressionService)
    {
        this.progressionService = progressionService;
    }

    [HttpGet("progression")]
    public async Task<IActionResult> GetProgression()
    {
        var progression = await progressionService.GetProgressionAsync();
        return Ok(progression.ToResponse());
    }

    /// <summary>Activities (gap-clustered sessions) within the progression window, newest first.</summary>
    [HttpGet("activities")]
    public async Task<IActionResult> GetActivities()
    {
        var activities = await progressionService.GetActivitiesAsync();
        return Ok(activities.Select(a => a.ToResponse()).ToList());
    }

    [HttpGet("activities/{id:guid}")]
    public async Task<IActionResult> GetActivity(Guid id)
    {
        var detail = await progressionService.GetActivityAsync(id);
        if (detail is null)
        {
            return NotFound(new ApiErrorResponse("Activity not found."));
        }

        return Ok(detail.ToResponse());
    }

    /// <summary>Per-day intensity for the last <paramref name="weeks"/> weeks.</summary>
    [HttpGet("activity-grid")]
    public async Task<IActionResult> GetActivityGrid([FromQuery] int weeks = 20)
    {
        var grid = await progressionService.GetActivityGridAsync(Math.Clamp(weeks, 1, MaxWeeks));
        return Ok(grid.Select(d => d.ToResponse()).ToList());
    }
}
