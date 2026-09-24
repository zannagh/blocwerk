// <copyright file="WallHoldShapesController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The hold-shape actions of the wall settings over the machine API, calling the same services as the buttons:
/// the outline upgrade (circles → traced outlines on the panel photos; preview, apply, exact revert — it changes
/// panel hold shapes, so it only ever runs when a caller asks for it) and "refine 3D hold shapes" (the derived
/// contact footprints on the model). Authorised like <see cref="WallGeometryPlacementController"/>: a wall key for
/// the wall, or a personal key with write access, whose owner is an admin of the wall; kiosks are refused.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/holds")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallHoldShapesController(
    IHoldOutlineUpgradeService outlines,
    IHoldFootprintService footprints,
    ILogger<WallHoldShapesController> logger) : WallAdminApiController(logger)
{
    /// <summary>Whether the outline upgrade is available here, and the wall's latest run (for a revert).</summary>
    [HttpGet("outline-upgrade")]
    public Task<IActionResult> OutlineStatus(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await outlines.GetStatusAsync(wallId, ct)));

    /// <summary>Outlines the circle holds without writing anything and returns what would happen.</summary>
    [HttpPost("outline-upgrade/preview")]
    public Task<IActionResult> PreviewOutlines(Guid wallId, [FromBody] OutlineUpgradeRequest? request, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await outlines.PreviewAsync(wallId, Options(request), ct)));

    /// <summary>Writes the outlines and records the run (idempotent: outlined holds are never touched again).</summary>
    [HttpPost("outline-upgrade")]
    public Task<IActionResult> ApplyOutlines(Guid wallId, [FromBody] OutlineUpgradeRequest? request, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await outlines.ApplyAsync(wallId, Options(request), ct)));

    /// <summary>Restores the holds a run changed that nobody edited since.</summary>
    [HttpPost("outline-upgrade/{runId:guid}/revert")]
    public Task<IActionResult> RevertOutlines(Guid wallId, Guid runId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await outlines.RevertAsync(wallId, runId, ct)));

    /// <summary>"Refine 3D hold shapes": the holds' contact footprints on the active model, from the capture photos.</summary>
    [HttpPost("refine-shapes")]
    public Task<IActionResult> RefineShapes(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await footprints.RefineAsync(wallId, ct)));

    private static HoldOutlineUpgradeOptions Options(OutlineUpgradeRequest? request) => new(request?.IncludeManual ?? false);
}
