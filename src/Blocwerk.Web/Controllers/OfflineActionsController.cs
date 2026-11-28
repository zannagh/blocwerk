using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// REST surface for the mutations the client-side offline queue can replay. Every action
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
    private readonly IAttemptService attemptService;
    private readonly IBoulderFeedbackService feedbackService;
    private readonly ICommentService commentService;
    private readonly ICurrentUserService currentUser;

    public OfflineActionsController(
        IAttemptService attemptService,
        IBoulderFeedbackService feedbackService,
        ICommentService commentService,
        ICurrentUserService currentUser)
    {
        this.attemptService = attemptService;
        this.feedbackService = feedbackService;
        this.commentService = commentService;
        this.currentUser = currentUser;
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
    [ValidateAntiForgeryToken]
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
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SetRating([FromBody] SetRatingRequest request)
    {
        return ExecuteAsync(request, async () =>
        {
            await feedbackService.SetRatingAsync(request.BoulderId, request.Stars);
            return new { stars = request.Stars };
        });
    }

    [HttpPost("favorites")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SetFavorite([FromBody] SetFavoriteRequest request)
    {
        // Deliberately absolute, never a toggle: a toggle replayed twice flips back.
        return ExecuteAsync(request, async () =>
        {
            var favorite = await feedbackService.SetFavoriteAsync(request.BoulderId, request.Favorite);
            return new { favorite };
        });
    }

    [HttpPost("projects")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SetProject([FromBody] SetProjectRequest request, [FromServices] IBoulderProjectService projectService)
    {
        // Absolute, like the favourite: a replay of the same entry is a no-op.
        return ExecuteAsync(request, async () =>
        {
            var project = await projectService.SetProjectAsync(request.BoulderId, request.Project);
            return new { project };
        });
    }

    [HttpPost("comments")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddComment([FromBody] AddCommentRequest request)
    {
        if (OfflineActionRunner.CommentTextError(request.Text) is { } error)
        {
            return Task.FromResult<IActionResult>(OfflineActionRunner.Permanent(error));
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
    /// Runs an action through <see cref="OfflineActionRunner"/>, after the checks only the browser's queue needs: a boulder
    /// id, and that the person the action was queued for is the person signed in now.
    /// </summary>
    private Task<IActionResult> ExecuteAsync(
        OfflineActionRequest request,
        Func<Task<object>> action)
    {
        if (request.BoulderId == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(OfflineActionRunner.Permanent("A boulder id is required."));
        }

        return OfflineActionRunner.RunAsync(request.BoulderId, async () =>
        {
            // Before anything is written: the person this action was queued FOR has to be the person
            // the request is authenticated AS. 409 rather than a 4xx the client treats as permanent,
            // because the action is still perfectly valid — just not for whoever is signed in now.
            if (!await OfflineActionOwnership.MatchesCallerAsync(currentUser, request.QueuedForUserId))
            {
                return StatusCode(
                    StatusCodes.Status409Conflict,
                    new OfflineActionError(OfflineActionOwnership.MismatchMessage, false));
            }

            return OfflineActionRunner.Applied(request.ClientRequestId, await action());
        });
    }
}
