using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// REST surface for the two boulder mutations the client-side offline queue can replay: create
/// and revise. Both are reachable without a live Blazor circuit, so a queued submit still lands
/// after the SignalR connection has dropped.
/// <para>
/// Idempotency mirrors <see cref="OfflineActionsController"/> but keys off the boulder id rather
/// than a per-row client request id. The client mints the boulder <see cref="Boulder.Id"/>, so a
/// replayed create is an upsert on that id and a replayed revise re-applies the same holds and
/// fields (a no-op). Neither can ever produce a duplicate, so both answer 200 on replay and never
/// 409.
/// </para>
/// </summary>
[ApiController]
[Route("api/offline/boulders")]
[Authorize]
[RequireClientHeader]
[Produces("application/json")]
public sealed class OfflineBouldersController : ControllerBase
{
    // Verbatim domain-guard messages the Core service throws. Only these are treated as permanent;
    // anything else surfacing as InvalidOperationException is infrastructure and stays retryable,
    // because EF Core and Npgsql also raise it for transient connection failures.
    private static readonly Dictionary<string, int> PermanentGuards = new(StringComparer.Ordinal)
    {
        ["Boulder not found"] = StatusCodes.Status404NotFound,
        ["Wall not found"] = StatusCodes.Status404NotFound,
        ["Only the creator can revise a boulder"] = StatusCodes.Status403Forbidden,
        [BoulderService.SentByOthersRevisionMessage] = StatusCodes.Status403Forbidden,
        ["Boulder is not historic"] = StatusCodes.Status400BadRequest,
        ["Select at least one hold"] = StatusCodes.Status400BadRequest,
    };

    private readonly IBoulderService boulderService;

    public OfflineBouldersController(IBoulderService boulderService)
    {
        this.boulderService = boulderService;
    }

    /// <summary>
    /// Creates (or, on replay, returns) the boulder the client minted the id for. Returns the
    /// canonical boulder so the client can reconcile its optimistic state with server truth.
    /// </summary>
    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateBoulderRequest request)
    {
        if (request.Id == Guid.Empty || request.WallId == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(Permanent("A boulder id and wall id are required."));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Task.FromResult<IActionResult>(Permanent("A boulder name is required."));
        }

        return ExecuteAsync(request.ClientRequestId, async () =>
        {
            var boulder = await boulderService.CreateBoulderAsync(
                request.WallId,
                request.Name,
                request.Grade,
                request.ToInputs(),
                request.IsDraft,
                request.KickboardFootholdsOn,
                request.HandsFollowFeet,
                request.FootColorOnly,
                request.Id,
                request.NoMatch);

            return Canonical(boulder);
        });
    }

    /// <summary>
    /// Revises (remaps) the boulder addressed by <paramref name="id"/>. Replaying the same
    /// snapshot re-applies the same state and returns 200 rather than failing.
    /// </summary>
    [HttpPost("{id:guid}")]
    public Task<IActionResult> Revise(Guid id, [FromBody] ReviseBoulderRequest request)
    {
        if (id == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(Permanent("A boulder id is required."));
        }

        return ExecuteAsync(request.ClientRequestId, async () =>
        {
            var boulder = await boulderService.ReviseBoulderAsync(
                id,
                request.ToInputs(),
                request.Name,
                request.Grade,
                request.KickboardFootholdsOn,
                request.HandsFollowFeet,
                request.FootColorOnly ?? string.Empty, // "" clears the foot colour server-side; null would mean "leave unchanged".
                request.NoMatch);

            return Canonical(boulder);
        });
    }

    private static object Canonical(Boulder boulder) => new
    {
        boulderId = boulder.Id,
        wallId = boulder.WallId,
        name = boulder.Name,
        grade = boulder.Grade,
        isDraft = boulder.IsDraft,
        holdCount = boulder.BoulderHolds.Count,
    };

    /// <summary>
    /// Runs an action and maps domain exceptions onto the status codes the client queue branches
    /// on. A replayed-but-already-applied create/revise reaches the success path (the service
    /// returns the stored boulder rather than throwing), so it answers 200 and the queue clears
    /// the entry.
    /// </summary>
    private async Task<IActionResult> ExecuteAsync(Guid? clientRequestId, Func<Task<object>> action)
    {
        try
        {
            var result = await action();
            return Ok(new OfflineActionResponse(true, clientRequestId, result));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new OfflineActionError("Your session has expired. Sign in to sync.", false));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Permanent(ex.Message);
        }
        catch (InvalidOperationException ex) when (PermanentGuards.ContainsKey(ex.Message))
        {
            return StatusCode(PermanentGuards[ex.Message], new OfflineActionError(ex.Message, true));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Offline boulder action failed");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new OfflineActionError("The boulder could not be saved. It stays queued.", false));
        }
    }

    private ObjectResult Permanent(string message)
    {
        return StatusCode(
            StatusCodes.Status400BadRequest,
            new OfflineActionError(message, true));
    }
}
