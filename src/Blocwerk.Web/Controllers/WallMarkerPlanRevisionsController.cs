// <copyright file="WallMarkerPlanRevisionsController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The marker plan's revisions over the machine API, and the API twin of the planner's "Markers swapped on
/// the wall" action (<see cref="IMarkerPlanService.SetRevisionEffectiveAsync"/>). A wall-admin route like the
/// capture API: a wall key for the wall in the route or a personal key, either created with write access, whose
/// owner must be an admin of the wall — enforced inside the service. No device needs it, so a sensor's or
/// camera's wall key without write access is refused.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/marker-plan/revisions")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallMarkerPlanRevisionsController : WallAdminApiController
{
    private readonly IMarkerPlanService plans;
    private readonly ApiWriteAudit audit;

    public WallMarkerPlanRevisionsController(
        IMarkerPlanService markerPlans, ApiWriteAudit writeAudit, ILogger<WallMarkerPlanRevisionsController> logger)
        : base(logger)
    {
        plans = markerPlans;
        audit = writeAudit;
    }

    /// <summary>The wall's plan revisions, newest first, with when each went up on the wall.</summary>
    [HttpGet]
    public Task<IActionResult> List(Guid wallId) => RunAsync(wallId, async () =>
    {
        var revisions = await plans.GetRevisionsAsync(wallId);
        return Ok(revisions.Select(MarkerPlanRevisionResponse.From));
    });

    /// <summary>
    /// Records when the revision's markers were put up on the wall (default: now); <c>effectiveFrom: null</c> with
    /// <c>clear: true</c> marks it as planned, not yet on the wall.
    /// </summary>
    [HttpPut("{revision:int}/effective")]
    [Consumes("application/json")]
    public Task<IActionResult> SetEffective(Guid wallId, int revision, [FromBody] MarkerPlanEffectiveRequest body) =>
        RunAsync(wallId, () => audit.RunAsync(User, wallId, $"marker-plan.effective rev:{revision}", async () =>
        {
            DateTimeOffset? from = body.Clear ? null : body.EffectiveFrom ?? DateTimeOffset.UtcNow;
            return await plans.SetRevisionEffectiveAsync(wallId, revision, from)
                ? NoContent()
                : NotFound(new ApiErrorResponse($"The wall has no marker plan revision {revision}."));
        }));
}

/// <summary>Body of <c>PUT …/marker-plan/revisions/{revision}/effective</c>.</summary>
/// <param name="EffectiveFrom">When the markers went up; null = now (unless <paramref name="Clear"/>).</param>
/// <param name="Clear">True to mark the revision as planned, not yet on the wall.</param>
public sealed record MarkerPlanEffectiveRequest(DateTimeOffset? EffectiveFrom = null, bool Clear = false);

/// <summary>One plan revision over the API.</summary>
public sealed record MarkerPlanRevisionResponse(
    int Revision, DateTimeOffset CreatedAt, int Markers, bool IsCurrent, bool UsedByActiveModel, DateTimeOffset? EffectiveFrom, bool IsOnWall)
{
    /// <summary>The API shape of <paramref name="r"/>.</summary>
    public static MarkerPlanRevisionResponse From(MarkerPlanRevisionInfo r) =>
        new(r.Revision, r.CreatedAt, r.Markers, r.IsCurrent, r.UsedByActiveModel, r.EffectiveFrom, r.IsOnWall);
}
