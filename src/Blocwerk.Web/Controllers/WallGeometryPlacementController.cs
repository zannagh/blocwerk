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
/// methods the wall-settings button calls, so the rules are identical: a wall API key for the wall in the route,
/// or a PERSONAL key created with write access (<c>ApiKey.AllowWrite</c>), in both cases only when the key's
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

    /// <summary>Reverts a run: every hold it placed that nobody moved since gets its previous values back.</summary>
    [HttpPost("{runId:guid}/revert")]
    public Task<IActionResult> Revert(Guid wallId, Guid runId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await placement.RevertAsync(wallId, runId, ct)));
}
