// <copyright file="WallMarkersController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Preparing a wall for marker captures over the machine API: the marker settings' "Save" (markers on or off, marker
/// size; <see cref="IWallGlyphService.SetGlyphSettingsAsync"/>) and the marker planner's plan upload
/// (<see cref="IMarkerPlanService.SavePlanAsync"/>), plus the state they leave. Marking a revision as put up on the wall
/// stays <c>PUT …/marker-plan/revisions/{revision}/effective</c> (<see cref="WallMarkerPlanRevisionsController"/>).
/// </summary>
/// <remarks>
/// Authorised like the other wall-admin routes (<see cref="WallAdminApiController"/>): a wall key for the wall in the
/// route or a personal key, either created with write access, whose owner must be an admin of the wall — the services'
/// check, kiosks refused there. Writes are audited in the change journal (<see cref="ApiWriteAudit"/>).
/// </remarks>
[ApiController]
[Route("api/walls/{wallId:guid}")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallMarkersController(
    IWallGlyphService glyphs,
    IMarkerPlanService plans,
    ApiWriteAudit audit,
    ILogger<WallMarkersController> logger) : WallAdminApiController(logger)
{
    /// <summary>Markers on or off, their size, and the marker plan's revisions (which is current, which is on the wall).</summary>
    [HttpGet("markers")]
    public Task<IActionResult> State(Guid wallId) => RunAsync(wallId, async () =>
    {
        var settings = await glyphs.GetGlyphSettingsAsync(wallId);
        var revisions = await plans.GetRevisionsAsync(wallId);
        return Ok(new WallMarkerStateResponse(
            settings.Enabled,
            settings.MarkerSizeMm,
            revisions.FirstOrDefault(r => r.IsCurrent)?.Revision,
            revisions.FirstOrDefault(r => r.IsOnWall)?.Revision,
            revisions.Select(MarkerPlanRevisionResponse.From).ToList()));
    });

    /// <summary>Switches the wall's markers on or off and sets their size (null keeps it). 400 for a size outside (0, 1000] mm.</summary>
    [HttpPut("markers")]
    [Consumes("application/json")]
    public Task<IActionResult> SetMarkers(Guid wallId, [FromBody] WallMarkerSettingsRequest body) =>
        RunAsync(wallId, () => audit.RunAsync(User, wallId, "markers.set", async () =>
        {
            try
            {
                return Ok(await glyphs.SetGlyphSettingsAsync(wallId, body.Enabled, body.MarkerSizeMm));
            }
            catch (ArgumentOutOfRangeException)
            {
                return BadRequest(new ApiErrorResponse($"Marker size must be between 0 and {WallGeometryValidator.MaxMarkerSizeMm:0} mm."));
            }
        }));

    /// <summary>The wall's current marker plan (marker-plan.json), or 404.</summary>
    [HttpGet("marker-plan")]
    public Task<IActionResult> Plan(Guid wallId) => RunAsync(wallId, async () =>
        await plans.GetPlanAsync(wallId) is { } plan
            ? Content(plans.ToJson(plan), "application/json")
            : NotFound(new ApiErrorResponse("This wall has no marker plan.")));

    /// <summary>
    /// Uploads a marker-plan.json as the wall's next plan revision, exactly like the planner's Save: 200 with the revision
    /// (the same plan again adds none), 422 with the reasons when it cannot be read or has errors.
    /// </summary>
    [HttpPut("marker-plan")]
    [Consumes("application/json")]
    public Task<IActionResult> SavePlan(Guid wallId, [FromBody] JsonElement body) =>
        RunAsync(wallId, () => audit.RunAsync(User, wallId, "marker-plan.save", async () =>
        {
            var plan = plans.FromJson(body.GetRawText(), out var errors);
            if (plan is null)
            {
                var issues = errors.Select(e => new PlanIssue(PlanIssueSeverity.Error, "plan-shape", e, null, null)).ToList();
                return UnprocessableEntity(new WallMarkerPlanSaveResponse(false, null, false, issues));
            }

            var result = await plans.SavePlanAsync(wallId, plan);
            var response = new WallMarkerPlanSaveResponse(result.Saved, result.Revision, result.Unchanged, result.Issues);
            return result.Saved ? Ok(response) : UnprocessableEntity(response);
        }));
}
