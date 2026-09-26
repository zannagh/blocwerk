// <copyright file="WallVolumesController.cs" company="Blocwerk">
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
/// Volumes without markers over the machine API (<see cref="IWallVolumeService"/>): find them on the active model's
/// photo-real scene, list them, hide or show a wrong one, remove a false one (and undo), give them flat sides. Same key rules as the hold placement: a wall key for the
/// wall in the route or a personal key with write access, and the key's owner must be an admin of the wall.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/geometry/volumes")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallVolumesController(IWallVolumeService volumes, ILogger<WallVolumesController> logger) : WallScopedApiController
{
    /// <summary>Finds the volumes (replacing the model's previous ones) and places the holds on them.</summary>
    [HttpPost]
    public Task<IActionResult> Detect(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.DetectAsync(wallId, ct)));

    /// <summary>The active model's volumes, hidden ones included.</summary>
    [HttpGet]
    public Task<IActionResult> List(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.ListAsync(wallId, ct)));

    /// <summary>Hides a volume: it is not drawn and its holds go back to the facet.</summary>
    [HttpPost("{volumeId:guid}/hide")]
    public Task<IActionResult> Hide(Guid wallId, Guid volumeId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.SetHiddenAsync(wallId, volumeId, true, ct)));

    /// <summary>Shows a hidden volume again.</summary>
    [HttpPost("{volumeId:guid}/show")]
    public Task<IActionResult> Show(Guid wallId, Guid volumeId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.SetHiddenAsync(wallId, volumeId, false, ct)));

    /// <summary>Removes a falsely detected volume: holds go back to the facet and a re-detection does not create it again.</summary>
    [HttpPost("{volumeId:guid}/remove")]
    public Task<IActionResult> Remove(Guid wallId, Guid volumeId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.SetRemovedAsync(wallId, volumeId, true, ct)));

    /// <summary>Restores a removed volume (the undo).</summary>
    [HttpPost("{volumeId:guid}/restore")]
    public Task<IActionResult> Restore(Guid wallId, Guid volumeId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.SetRemovedAsync(wallId, volumeId, false, ct)));

    /// <summary>"Has flat sides" for one volume: planar faces from its outline and high points, or back to the height field.</summary>
    [HttpPut("{volumeId:guid}/flat-sides")]
    public Task<IActionResult> SetFlatSides(Guid wallId, Guid volumeId, [FromBody] FlatSidesRequest request, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.SetFlatSidesAsync(wallId, volumeId, request.Value, ct)));

    /// <summary>The wall setting "Volumes on this wall have flat sides".</summary>
    [HttpGet("flat-sides")]
    public Task<IActionResult> GetWallFlatSides(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(new FlatSidesRequest(await volumes.GetWallFlatSidesAsync(wallId, ct))));

    /// <summary>Sets the wall setting; with <c>applyToAll</c> also switches the existing volumes (on: where the faces fit well).</summary>
    [HttpPut("flat-sides")]
    public Task<IActionResult> SetWallFlatSides(Guid wallId, [FromBody] FlatSidesRequest request, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await volumes.SetWallFlatSidesAsync(wallId, request.Value, request.ApplyToAll, ct)));

    private async Task<IActionResult> RunAsync(Guid wallId, Func<Task<IActionResult>> action)
    {
        if (GuardWallOrPersonalKey(wallId) is { } guard)
        {
            return guard;
        }

        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("This API key's owner is not an admin of that wall."));
        }
        catch (KioskRestrictedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(ex.Message));
        }
        catch (UserFacingException ex)
        {
            return Conflict(new ApiErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Volume request on wall {WallId} failed unexpectedly", wallId);
            return Conflict(new ApiErrorResponse(UserFacingException.GenericMessage));
        }
    }
}
