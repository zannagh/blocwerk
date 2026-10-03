// <copyright file="HoldProposalsController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// "Find holds from all photos" over the machine API (<see cref="IHoldProposalService"/>): run the multi-view
/// search, list the proposals, accept one (a hold is created on its panel) or reject one (remembered), fetch a
/// proposal's review crop. Same key rules as the hold placement; the service checks the key owner's wall role.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/geometry/hold-proposals")]
[Authorize(Policy = BlocwerkPolicies.AnyApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class HoldProposalsController(IHoldProposalService proposals, ILogger<HoldProposalsController> logger) : WallScopedApiController
{
    /// <summary>Runs the search (minutes on the CPU); replaces the pending proposals.</summary>
    [HttpPost]
    public Task<IActionResult> Find(Guid wallId, CancellationToken ct) =>
        RunAsync(wallId, async () => Ok(await proposals.FindAsync(wallId, ct)));

    /// <summary>The proposals with a status (default pending).</summary>
    [HttpGet]
    public Task<IActionResult> List(Guid wallId, [FromQuery] HoldProposalStatus status = HoldProposalStatus.Pending, CancellationToken ct = default) =>
        RunAsync(wallId, async () => Ok((await proposals.ListAsync(wallId, status, ct)).Select(p => new
        {
            p.Id, p.FacetId, p.A, p.B, p.H, p.SizeMm, p.Views, p.Confidence, p.PanelId, p.PanelX, p.PanelY, p.PanelRadius, p.Status, p.HoldId,
            OnPanel = p.PanelId is not null,
        })));

    /// <summary>Accepts a proposal: the hold is created on its panel photo.</summary>
    [HttpPost("{proposalId:guid}/accept")]
    public Task<IActionResult> Accept(Guid wallId, Guid proposalId, [FromBody] HoldProposalAcceptRequest? body, CancellationToken ct) =>
        RunAsync(wallId, async () =>
        {
            var category = Enum.TryParse<HoldCategory>(body?.Category, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c : HoldCategory.Hand;
            var hold = await proposals.AcceptAsync(wallId, proposalId, body?.Color, category, ct);
            return Ok(new { hold.Id, hold.WallPanelId, hold.X, hold.Y, hold.Radius });
        });

    /// <summary>Rejects a proposal; the spot is not proposed again.</summary>
    [HttpPost("{proposalId:guid}/reject")]
    public Task<IActionResult> Reject(Guid wallId, Guid proposalId, CancellationToken ct) =>
        RunAsync(wallId, async () =>
        {
            await proposals.RejectAsync(wallId, proposalId, ct);
            return NoContent();
        });

    /// <summary>The review crop (JPEG) of a proposal's clearest capture photo.</summary>
    [HttpGet("{proposalId:guid}/crop")]
    [Produces("image/jpeg")]
    public Task<IActionResult> Crop(Guid wallId, Guid proposalId, CancellationToken ct) =>
        RunAsync(wallId, async () => await proposals.CropAsync(wallId, proposalId, ct) is { } jpeg ? File(jpeg, "image/jpeg") : NotFound());

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
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("This API key's owner may not do that on this wall."));
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
            logger.LogWarning(ex, "Hold proposal request on wall {WallId} failed unexpectedly", wallId);
            return Conflict(new ApiErrorResponse(UserFacingException.GenericMessage));
        }
    }
}
