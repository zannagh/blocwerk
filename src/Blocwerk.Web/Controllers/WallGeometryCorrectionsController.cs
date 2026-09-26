// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Corrections of the wall's active 3D model over the machine API — the same <see cref="IWallGeometryCorrectionService"/>
/// calls the geometry panel and the 3D view make: each creates a new active model version (the old one stays in the
/// history, revertable by activating it). Same key rules as the other wall-admin routes: a wall key for the wall in the
/// route or a personal key with write access, and the key's owner must be an admin of the wall; never a kiosk.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/geometry/corrections")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallGeometryCorrectionsController(
    IWallGeometryCorrectionService corrections, ILogger<WallGeometryCorrectionsController> logger) : WallAdminApiController(logger)
{
    /// <summary>Where the active model's sizes and angles come from, its surfaces and the photos a distance can be measured on.</summary>
    [HttpGet]
    public Task<IActionResult> State(Guid wallId) =>
        RunAsync(wallId, async () => await corrections.GetStateAsync(wallId) is { } state
            ? Ok(state)
            : NotFound(new ApiErrorResponse("This wall has no 3D model yet.")));

    /// <summary>
    /// "Make sizes exact": {photoIndex, a: [x, y], b: [x, y], mm} — two points on a photo of the model's capture (stored-photo
    /// pixels) and the millimetres between them. 409 with the reason when the points or the distance are unusable.
    /// </summary>
    [HttpPost("scale")]
    public Task<IActionResult> Scale(Guid wallId, [FromBody] CaptureScaleReference reference) =>
        RunAsync(wallId, async () => Ok(await corrections.MakeSizesExactAsync(wallId, reference)));

    /// <summary>"This surface is vertical": {facetId}. The model turns so that surface is plumb.</summary>
    [HttpPost("vertical")]
    public Task<IActionResult> Vertical(Guid wallId, [FromBody] GeometryFacetRequest request) =>
        RunAsync(wallId, async () => Ok(await corrections.SetVerticalSurfaceAsync(wallId, request.FacetId)));

    /// <summary>"Not part of the wall": {facetId}. The surface is dropped from the model.</summary>
    [HttpPost("drop")]
    public Task<IActionResult> Drop(Guid wallId, [FromBody] GeometryFacetRequest request) =>
        RunAsync(wallId, async () => Ok(await corrections.DropSurfaceAsync(wallId, request.FacetId)));
}
