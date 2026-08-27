using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// REST surface for the four mutations the client-side offline queue can replay. Every action
/// is reachable without a live Blazor circuit, so a queued click still lands after the SignalR
/// connection has dropped, and every action is idempotent under replay of the same
/// <c>clientRequestId</c>.
/// </summary>
[ApiController]
[Route("api/offline")]
[Authorize]
[RequireClientHeader]
[Produces("application/json")]
public sealed class OfflineActionsController : ControllerBase
{
    // Verbatim messages the Core services throw for their two permanent domain guards. Anything
    // else surfacing as InvalidOperationException is infrastructure and must stay retryable.
    private const string BoulderNotFound = "Boulder not found";
    private const string NotAWallMember = "Only wall members can do this";

    private readonly IAttemptService attemptService;
    private readonly IBoulderFeedbackService feedbackService;
    private readonly ICommentService commentService;

    public OfflineActionsController(
        IAttemptService attemptService,
        IBoulderFeedbackService feedbackService,
        ICommentService commentService)
    {
        this.attemptService = attemptService;
        this.feedbackService = feedbackService;
        this.commentService = commentService;
    }

    /// <summary>
    /// Cheap authenticated probe. The queue calls this before a flush after a long offline
    /// period so an expired session surfaces as a re-login prompt rather than as a burst of
    /// failing action posts.
    /// </summary>
    [HttpGet("session")]
    public IActionResult Session()
    {
        return Ok(new { authenticated = true });
    }

    [HttpPost("attempts")]
    public Task<IActionResult> LogAttempt([FromBody] LogAttemptRequest request)
    {
        return ExecuteAsync(request, async () =>
        {
            var attempt = await attemptService.LogAttemptAsync(
                request.BoulderId,
                request.Type,
                request.Notes,
                request.ClientRequestId,
                request.Timestamp);

            return new { attemptId = attempt.Id, type = attempt.Type.ToString() };
        });
    }

    [HttpPost("ratings")]
    public Task<IActionResult> SetRating([FromBody] SetRatingRequest request)
    {
        return ExecuteAsync(request, async () =>
        {
            await feedbackService.SetRatingAsync(request.BoulderId, request.Stars);
            return new { stars = request.Stars };
        });
    }

    [HttpPost("favorites")]
    public Task<IActionResult> SetFavorite([FromBody] SetFavoriteRequest request)
    {
        // Deliberately absolute, never a toggle: a toggle replayed twice flips back.
        return ExecuteAsync(request, async () =>
        {
            var favorite = await feedbackService.SetFavoriteAsync(request.BoulderId, request.Favorite);
            return new { favorite };
        });
    }

    [HttpPost("comments")]
    public Task<IActionResult> AddComment([FromBody] AddCommentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Task.FromResult<IActionResult>(Permanent("Comment text must not be empty."));
        }

        return ExecuteAsync(request, async () =>
        {
            var comment = await commentService.AddCommentAsync(
                request.BoulderId,
                request.Text,
                request.ClientRequestId);

            return new { commentId = comment.Id };
        });
    }

    /// <summary>
    /// Runs an action and maps domain exceptions onto the status codes the client queue
    /// branches on. A replayed-but-already-applied action reaches this method's success path
    /// (the services return the stored row rather than throwing), so it answers 200 and the
    /// queue can clear the entry.
    /// </summary>
    private async Task<IActionResult> ExecuteAsync(
        OfflineActionRequest request,
        Func<Task<object>> action)
    {
        if (request.BoulderId == Guid.Empty)
        {
            return Permanent("A boulder id is required.");
        }

        try
        {
            var result = await action();
            return Ok(new OfflineActionResponse(true, request.ClientRequestId, result));
        }
        catch (UnauthorizedAccessException)
        {
            // Session gone. Retryable in principle: the queue pauses and prompts re-login.
            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new OfflineActionError("Your session has expired. Sign in to sync.", false));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Permanent(ex.Message);
        }
        catch (InvalidOperationException ex) when (IsDomainGuard(ex))
        {
            // Only the two known domain guards are treated as permanent. Matching loosely here
            // would be dangerous: EF Core and Npgsql also raise InvalidOperationException for
            // transient connection failures, and classifying one of those as permanent would
            // make the client drop a perfectly valid queued action.
            var notFound = ex.Message.Equals(BoulderNotFound, StringComparison.Ordinal);
            return StatusCode(
                notFound ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden,
                new OfflineActionError(ex.Message, true));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Offline action failed for boulder {BoulderId}", request.BoulderId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new OfflineActionError("The action could not be saved. It stays queued.", false));
        }
    }

    private static bool IsDomainGuard(InvalidOperationException ex)
    {
        return ex.Message.Equals(BoulderNotFound, StringComparison.Ordinal)
               || ex.Message.Equals(NotAWallMember, StringComparison.Ordinal);
    }

    private ObjectResult Permanent(string message)
    {
        return StatusCode(
            StatusCodes.Status400BadRequest,
            new OfflineActionError(message, true));
    }
}
