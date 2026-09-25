// <copyright file="WallCaptureCoverageController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Capture.Coverage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// A capture's coverage report over the machine API: which parts of the wall its cameras saw well, which volume
/// faces and facets fell short, the markers per facet, the video against the recipe, and the plain-words "what to
/// add" list for the next capture. Authorised exactly like <see cref="WallCapturesController"/>; a capture is only
/// served under its own wall.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/captures")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallCaptureCoverageController(
    ICaptureCoverageService coverage,
    ILogger<WallCaptureCoverageController> logger) : WallAdminApiController(logger)
{
    /// <summary>
    /// The capture's coverage report. 404 when the wall has no such capture, or when it has no report (not done yet,
    /// or no model).
    /// </summary>
    [HttpGet("{captureId:guid}/coverage")]
    public Task<IActionResult> Get(Guid wallId, Guid captureId, CancellationToken ct) =>
        RunAsync(wallId, async () =>
        {
            var lookup = await coverage.GetAsync(wallId, captureId, ct);
            if (!lookup.CaptureFound)
            {
                return NotFound(new ApiErrorResponse("There is no such capture on this wall."));
            }

            return lookup.Report is { } report
                ? Content(report.ToJson(), "application/json")
                : NotFound(new ApiErrorResponse("This capture has no coverage report yet: it is computed once the capture is done."));
        });
}
