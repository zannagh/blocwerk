// <copyright file="WallGeometryPlacementController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// "Place existing holds on the 3D model" over the machine API — the same <see cref="IHoldTexturePlacementService"/>
/// methods the wall-settings button calls, so the rules are identical: a wall API key for the wall in the route
/// or a PERSONAL key, either created with write access (<c>ApiKey.AllowWrite</c>), and only when the key's
/// OWNER is an admin of the wall (the service's own check). Never a kiosk or installation key (neither
/// satisfies <see cref="BlocwerkPolicies.AnyApiKey"/>, and the service refuses kiosk sessions besides).
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/geometry/place-holds")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallGeometryPlacementController(
    IHoldTexturePlacementService placement, ILogger<WallGeometryPlacementController> logger) : WallAdminApiController(logger)
{
    /// <summary>Registers the panel photos onto the active model's textures and places the holds. Takes a while.</summary>
    [HttpPost]
    public Task<IActionResult> Place(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.PlaceAsync(wallId, HoldPlacementTrigger.Api, ct)));

    /// <summary>Whether the action is available on the wall, and its latest run (with the run id to revert).</summary>
    [HttpGet]
    public Task<IActionResult> Status(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.GetStatusAsync(wallId, ct)));

    /// <summary>Dry run of "re-place outlier holds in 3D": how many placed holds the wall contradicts, per panel photo.</summary>
    [HttpGet("outliers")]
    public Task<IActionResult> PreviewOutliers(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.PreviewOutliersAsync(wallId, ct)));

    /// <summary>Re-places those holds in 3D only (revert the run like any other).</summary>
    [HttpPost("outliers")]
    public Task<IActionResult> ReplaceOutliers(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.ReplaceOutliersAsync(wallId, ct)));

    /// <summary>The latest panel registration on a 3D runner (for surface placement).</summary>
    [HttpGet("surface/registration")]
    public Task<IActionResult> SurfaceRegistration(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.GetSurfaceRegistrationAsync(wallId, ct)));

    /// <summary>Queues the panel photos for registration on a 3D runner.</summary>
    [HttpPost("surface/registration")]
    public Task<IActionResult> RegisterPanels(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.RegisterPanelsAsync(wallId, ct)));

    /// <summary>Dry run of "place holds on the 3D surface".</summary>
    [HttpGet("surface")]
    public Task<IActionResult> PreviewSurface(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.PreviewSurfaceAsync(wallId, ct)));

    /// <summary>Places the holds on the 3D surface, 3D fields only (revert the run like any other).</summary>
    [HttpPost("surface")]
    public Task<IActionResult> PlaceOnSurface(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.PlaceOnSurfaceAsync(wallId, ct)));

    /// <summary>Reverts a run: every hold it placed that nobody moved since gets its previous values back.</summary>
    [HttpPost("{runId:guid}/revert")]
    public Task<IActionResult> Revert(Guid wallId, Guid runId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.RevertAsync(wallId, runId, ct)));
}
