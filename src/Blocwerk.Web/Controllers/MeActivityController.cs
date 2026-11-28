// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The Activity page's own edits and its day panel for the companion app, through the page's
/// <see cref="IProgressionService"/> calls: the progression window and grouping (saved per user, as the page saves them),
/// an activity's duration, and the summary of any day, also one outside the progression window.
/// </summary>
/// <remarks>
/// A linked device key only, never cached. The read takes the feed's rate limit, the edits the per-key write limit.
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class MeActivityController : ControllerBase
{
    private const int MinWindowDays = 7;
    private const int MaxWindowDays = 365;
    private const int MaxDurationMinutes = 24 * 60;

    private readonly IProgressionService progression;

    public MeActivityController(IProgressionService progression)
    {
        this.progression = progression;
    }

    /// <summary>Saves the progression window and/or grouping and answers the progression under them.</summary>
    [HttpPatch("progression")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> SetProgressionPreference([FromBody] ProgressionPreferenceRequest request)
    {
        if (request.WindowDays is < MinWindowDays or > MaxWindowDays)
        {
            return BadRequest(new ApiErrorResponse($"windowDays must be {MinWindowDays} to {MaxWindowDays}."));
        }

        ProgressionGroupBy? groupBy = null;
        if (request.GroupBy is { } name)
        {
            if (!Enum.TryParse<ProgressionGroupBy>(name, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)
                || int.TryParse(name, out _))
            {
                return BadRequest(new ApiErrorResponse("groupBy must be Day, Week or Month."));
            }

            groupBy = parsed;
        }

        if (request.WindowDays is { } days)
        {
            await progression.UpdateProgressionWindowAsync(days);
        }

        if (groupBy is { } grouping)
        {
            await progression.UpdateProgressionGroupingAsync(grouping);
        }

        return Ok((await progression.GetProgressionAsync()).ToResponse());
    }

    /// <summary>Sets an activity's duration, as the activity page's Save does, and answers the activity.</summary>
    [HttpPatch("activities/{activityId:guid}")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> SetDuration(Guid activityId, [FromBody] ActivityDurationRequest request)
    {
        if (request.DurationMinutes is < 0 or > MaxDurationMinutes)
        {
            return BadRequest(new ApiErrorResponse($"durationMinutes must be 0 to {MaxDurationMinutes}."));
        }

        try
        {
            await progression.UpdateActivityDurationAsync(activityId, request.DurationMinutes);
        }
        catch (InvalidOperationException)
        {
            // "Activity not found": also someone else's activity.
            return NotFound(new ApiErrorResponse("Activity not found."));
        }

        return await progression.GetActivityAsync(activityId) is { } detail
            ? Ok(detail.ToResponse())
            : NotFound(new ApiErrorResponse("Activity not found."));
    }

    /// <summary>What the caller did on <paramref name="date"/> (yyyy-MM-dd, UTC), inside the progression window or not.</summary>
    [HttpGet("days/{date}")]
    [EnableRateLimiting(FeedEndpoints.RateLimitPolicy)]
    public async Task<IActionResult> GetDay(string date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return BadRequest(new ApiErrorResponse("date must be yyyy-MM-dd."));
        }

        var summary = await progression.GetDaySummaryAsync(day);
        return Ok(new DaySummaryResponse(
            summary.Date,
            summary.Boulders.Select(b => b.ToResponse()).ToList(),
            summary.Hangboard.Select(h => h.ToResponse()).ToList(),
            summary.Pullups.Select(p => p.ToResponse()).ToList(),
            summary.SessionDuration is { } duration ? (int)Math.Round(duration.TotalMinutes) : null));
    }
}
