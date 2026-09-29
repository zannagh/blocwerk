// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Base for the capture package routes (<see cref="ICapturePackageService"/>): replaying a finished capture from one
/// instance on another without training. Machine routes for the rollout script, so the scheme is pinned to the API key
/// (no cookie reaches them, no antiforgery applies) and the policy admits a PERSONAL key with write access only; the
/// service then requires the key's owner to be an app administrator, exactly as the administration area decides it.
/// </summary>
[ApiController]
[Authorize(Policy = BlocwerkPolicies.HumanOrUserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public abstract class CapturePackageApiController(ICapturePackageService packages, ILogger logger) : ControllerBase
{
    protected ICapturePackageService Packages { get; } = packages;

    /// <summary>Runs <paramref name="action"/>, mapping the service's refusals to 403/409.</summary>
    protected async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Only an administrator of this installation may move captures between instances."));
        }
        catch (CaptureFileTooLargeException ex)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ApiErrorResponse(ex.Message));
        }
        catch (UserFacingException ex)
        {
            return Conflict(new ApiErrorResponse(ex.Message));
        }
        catch (JsonException ex)
        {
            return BadRequest(new ApiErrorResponse($"The package is not readable JSON: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Capture package request failed unexpectedly");
            return Conflict(new ApiErrorResponse(UserFacingException.GenericMessage));
        }
    }
}
