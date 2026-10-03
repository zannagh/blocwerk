// <copyright file="WallAdminApiController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Base for the wall-admin machine API (3D model, captures, hold shapes, marker plan): the wall guard of
/// <see cref="WallScopedApiController.GuardWallOrPersonalKey"/> — a wall key for the wall in the route or a
/// personal key, either created with write access — and one error mapping. The services decide the rest from the key's OWNER,
/// exactly as for the browser: wall admin, never a kiosk. Every controller that admits
/// <see cref="BlocwerkPolicies.AnyApiKey"/> derives from this class (pinned by a test).
/// </summary>
public abstract class WallAdminApiController(ILogger logger) : WallScopedApiController
{
    /// <summary>
    /// Hides the device guard from wall-admin controllers: it admits ANY wall key bound to the wall, write access or
    /// not, so a wall-admin action calling it would reopen the device-key hole. Use <see cref="RunAsync"/>.
    /// </summary>
    [Obsolete("Wall-admin actions must use RunAsync (GuardWallOrPersonalKey), never the device guard.", error: true)]
    protected new IActionResult? GuardWall(Guid wallId) => base.GuardWall(wallId);

    /// <summary>The concrete controller's logger.</summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>Runs <paramref name="action"/> after the wall guard, mapping the services' refusals to 403/409.</summary>
    protected async Task<IActionResult> RunAsync(Guid wallId, Func<Task<IActionResult>> action)
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
            // Written for the caller (no model, already reverted, a run in progress, …): safe to echo.
            return Conflict(new ApiErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Anything else may be EF Core or framework internals: log it, never echo it.
            Logger.LogWarning(ex, "Wall admin API request on wall {WallId} failed unexpectedly", wallId);
            return Conflict(new ApiErrorResponse(UserFacingException.GenericMessage));
        }
    }
}
