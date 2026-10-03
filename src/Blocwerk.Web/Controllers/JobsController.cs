// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// <c>GET /api/v1/admin/jobs</c>: the long-running jobs (captures per stage, GPU training, finishing, follow-up steps,
/// texture re-renders, re-solves, imports) with state, percentage, remaining time, timestamps and the last error. Read-only,
/// so a PERSONAL key without write access is enough; the scheme is pinned to the API key (no cookie, no antiforgery). The
/// service decides what the key's owner sees: every wall for an administrator of the installation, the walls they
/// administer otherwise. Wall, kiosk and installation keys are refused by the policy.
/// </summary>
[ApiController]
[Route("api/v1/admin/jobs")]
[Authorize(Policy = BlocwerkPolicies.UserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed class JobsController(IJobProgressService jobs, ILogger<JobsController> logger) : ControllerBase
{
    /// <summary>Running jobs and those that ended within <paramref name="recentHours"/> (default 24, at most 168).</summary>
    /// <param name="wallId">Only this wall's jobs (403 when the key's owner may not watch it).</param>
    /// <param name="recentHours">How far back ended jobs are listed; 0 lists running ones only.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot.</returns>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? wallId, [FromQuery] double? recentHours, CancellationToken ct)
    {
        if (recentHours is { } hours && (!double.IsFinite(hours) || hours < 0))
        {
            return BadRequest(new ApiErrorResponse("recentHours must be a number of hours, 0 or more."));
        }

        try
        {
            var recent = recentHours is { } h ? TimeSpan.FromHours(Math.Min(h, JobProgressService.MaxRecent.TotalHours)) : (TimeSpan?)null;
            return Ok(await jobs.ListAsync(wallId, recent, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("This API key's owner may not watch that wall's jobs."));
        }
        catch (KioskRestrictedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Listing the long-running jobs failed unexpectedly");
            return Conflict(new ApiErrorResponse(UserFacingException.GenericMessage));
        }
    }
}
