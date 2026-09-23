// <copyright file="WallUpdateShapesController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Drives the wall update's optional "recognise hold shapes" step and its review over the machine API —
/// the same <see cref="IWallUpdateShapeService"/> methods the wizard calls, so the rules are identical:
/// a wall API key for the wall in the route whose OWNER is an admin of it, never a kiosk (kiosk keys are a
/// different scope, and the service refuses kiosk sessions besides). Scheme pinned to API keys.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/update/shapes")]
[Authorize(Policy = BlocwerkPolicies.WallApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallUpdateShapesController : WallScopedApiController
{
    private readonly IWallUpdateShapeService shapes;
    private readonly ILogger<WallUpdateShapesController> logger;

    public WallUpdateShapesController(IWallUpdateShapeService shapes, ILogger<WallUpdateShapesController> logger)
    {
        this.shapes = shapes;
        this.logger = logger;
    }

    /// <summary>Starts (or resumes) recognition in the background. 202 with the run status; poll GET.</summary>
    [HttpPost("recognition")]
    [Consumes("application/json")]
    public Task<IActionResult> Start(Guid wallId, [FromBody] ShapeRecognitionStartRequest body, CancellationToken ct) =>
        RunAsync(wallId, async () =>
        {
            if (!TryParse<ShapeRecognitionScope>(body.Scope, ShapeRecognitionScope.NewAndChanged, out var scope))
            {
                return BadRequest(new ApiErrorResponse($"Unknown scope '{body.Scope}'."));
            }

            var status = await shapes.StartRecognitionAsync(
                wallId, new ShapeRecognitionOptions(scope, body.OverwriteManual, body.Rerun), body.SessionId, ct);
            return Accepted(status.ToResponse());
        });

    /// <summary>The run status and the review tally.</summary>
    [HttpGet("recognition")]
    public Task<IActionResult> Status(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok((await shapes.GetStatusAsync(wallId, ct)).ToResponse()));

    /// <summary>Skips the step: every hold promotes with the shape it has now.</summary>
    [HttpPost("recognition/skip")]
    public Task<IActionResult> Skip(Guid wallId, [FromBody] ShapeSessionRequest? body, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok((await shapes.SkipAsync(wallId, body?.SessionId, ct)).ToResponse()));

    /// <summary>Finishes the shape step (review done, or run skipped): the update moves on to its confirm step.</summary>
    [HttpPost("review/complete")]
    public Task<IActionResult> CompleteReview(Guid wallId, [FromBody] ShapeSessionRequest? body, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok((await shapes.CompleteReviewAsync(wallId, body?.SessionId, ct)).ToResponse()));

    /// <summary>The recognised shapes, lowest confidence first; <paramref name="below"/> keeps only those under it.</summary>
    [HttpGet("proposals")]
    public Task<IActionResult> Proposals(Guid wallId, [FromQuery] double? below, CancellationToken ct) =>
        RunAsync(wallId, async () =>
            Ok((await shapes.GetProposalsAsync(wallId, below, ct)).Select(p => p.ToResponse()).ToList()));

    /// <summary>Records review verdicts. Nothing reaches the live holds until the update is applied.</summary>
    [HttpPost("decisions")]
    [Consumes("application/json")]
    public Task<IActionResult> Decide(Guid wallId, [FromBody] ShapeDecisionsRequest body, CancellationToken ct) =>
        RunAsync(wallId, async () =>
        {
            var requests = new List<ShapeDecisionRequest>();
            foreach (var d in body.Decisions ?? [])
            {
                if (!TryParse<ShapeReviewDecision>(d.Decision, null, out var decision))
                {
                    return BadRequest(new ApiErrorResponse($"Unknown decision '{d.Decision}' for hold {d.HoldId}."));
                }

                requests.Add(new ShapeDecisionRequest(d.HoldId, decision, WallUpdateShapeMappings.ToShape(d.Shape)));
            }

            return Ok(new ShapeWriteResponse(await shapes.DecideAsync(wallId, requests, body.SessionId, ct)));
        });

    /// <summary>Accepts every still-pending recognised outline at or above the given confidence.</summary>
    [HttpPost("accept-above")]
    [Consumes("application/json")]
    public Task<IActionResult> AcceptAbove(Guid wallId, [FromBody] ShapeAcceptAboveRequest body, CancellationToken ct) =>
        RunAsync(wallId, async () =>
            Ok(new ShapeWriteResponse(await shapes.AcceptAboveAsync(wallId, body.MinConfidence, body.SessionId, ct))));

    private static bool TryParse<T>(string? value, T? fallback, out T parsed)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) && fallback is { } f)
        {
            parsed = f;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    /// <summary>The wall guard plus one error mapping for every action.</summary>
    private async Task<IActionResult> RunAsync(Guid wallId, Func<Task<IActionResult>> action)
    {
        if (GuardWall(wallId) is { } guard)
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
            // Written for the caller (stale session, step not finished, detection off): safe to echo.
            return Conflict(new ApiErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Anything else may be EF Core or framework internals: log it, never echo it.
            logger.LogWarning(ex, "Shape step request on wall {WallId} failed unexpectedly", wallId);
            return Conflict(new ApiErrorResponse(UserFacingException.GenericMessage));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message));
        }
    }
}
