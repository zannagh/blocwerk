// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The caller's own marks on a boulder for the companion app: rating, favourite, project and comments. The bodies, the
/// service calls, the validation and the answers are the offline web client's (<see cref="OfflineActionsController"/>,
/// through <see cref="OfflineActionRunner"/>), so a write from the app lands exactly as one from the browser. Attempts
/// and ticks are <c>/api/v1/me/attempts</c>.
/// </summary>
/// <remarks>
/// A linked device key only, rate limited per key (<see cref="Endpoints.DeviceKeyWriteRateLimiter"/>), never cached. Antiforgery does not apply: the policy pins the
/// API-key scheme, so a browser cookie never authorizes these routes. Every write is idempotent: rating, favourite and
/// project are absolute values (removing one not set is a no-op), and a comment carries a <c>clientRequestId</c>. A
/// comment already deleted answers 404, which a queue treats as permanent and drops.
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeyWriteRateLimit]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class MeBoulderMarksController : ControllerBase
{
    private readonly IBoulderFeedbackService feedback;
    private readonly IBoulderProjectService projects;
    private readonly ICommentService comments;

    public MeBoulderMarksController(IBoulderFeedbackService feedback, IBoulderProjectService projects, ICommentService comments)
    {
        this.feedback = feedback;
        this.projects = projects;
        this.comments = comments;
    }

    /// <summary>Sets the caller's star rating (1 to 5).</summary>
    [HttpPost("ratings")]
    public Task<IActionResult> SetRating([FromBody] SetRatingRequest request) =>
        RunAsync(request.BoulderId, request.ClientRequestId, async () =>
        {
            await feedback.SetRatingAsync(request.BoulderId, request.Stars);
            return new { stars = request.Stars };
        });

    /// <summary>Clears the caller's rating; clearing none is a no-op.</summary>
    [HttpDelete("ratings/{boulderId:guid}")]
    public Task<IActionResult> RemoveRating(Guid boulderId) =>
        RunAsync(boulderId, null, async () =>
        {
            await feedback.RemoveRatingAsync(boulderId);
            return new { stars = (int?)null };
        });

    /// <summary>Sets the favourite mark to <c>favorite</c> (absolute, never a toggle).</summary>
    [HttpPost("favorites")]
    public Task<IActionResult> SetFavorite([FromBody] SetFavoriteRequest request) =>
        RunAsync(request.BoulderId, request.ClientRequestId, async () =>
            new { favorite = await feedback.SetFavoriteAsync(request.BoulderId, request.Favorite) });

    /// <summary>Removes the favourite mark.</summary>
    [HttpDelete("favorites/{boulderId:guid}")]
    public Task<IActionResult> RemoveFavorite(Guid boulderId) =>
        RunAsync(boulderId, null, async () => new { favorite = await feedback.SetFavoriteAsync(boulderId, false) });

    /// <summary>Sets the project mark to <c>project</c> (absolute, never a toggle).</summary>
    [HttpPost("projects")]
    public Task<IActionResult> SetProject([FromBody] SetProjectRequest request) =>
        RunAsync(request.BoulderId, request.ClientRequestId, async () =>
            new { project = await projects.SetProjectAsync(request.BoulderId, request.Project) });

    /// <summary>Removes the project mark.</summary>
    [HttpDelete("projects/{boulderId:guid}")]
    public Task<IActionResult> RemoveProject(Guid boulderId) =>
        RunAsync(boulderId, null, async () => new { project = await projects.SetProjectAsync(boulderId, false) });

    /// <summary>Posts a comment. The same <c>clientRequestId</c> again returns the comment already stored.</summary>
    [HttpPost("comments")]
    public Task<IActionResult> AddComment([FromBody] AddCommentRequest request)
    {
        if (OfflineActionRunner.CommentTextError(request.Text) is { } error)
        {
            return Task.FromResult<IActionResult>(OfflineActionRunner.Permanent(error));
        }

        return RunAsync(request.BoulderId, request.ClientRequestId, async () =>
        {
            var comment = await comments.AddCommentAsync(request.BoulderId, request.Text, request.ClientRequestId);
            return new { commentId = comment.Id };
        });
    }

    /// <summary>Deletes one of the caller's own comments, as the web's delete button does.</summary>
    [HttpDelete("comments/{commentId:guid}")]
    public Task<IActionResult> DeleteComment(Guid commentId) =>
        OfflineActionRunner.RunAsync(
            commentId,
            async () =>
            {
                await comments.DeleteCommentAsync(commentId);
                return OfflineActionRunner.Applied(null, new { commentId });
            },
            hideBoulders: true);

    private static Task<IActionResult> RunAsync(Guid boulderId, Guid? clientRequestId, Func<Task<object>> write)
    {
        if (boulderId == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(OfflineActionRunner.Permanent("A boulder id is required."));
        }

        return OfflineActionRunner.RunAsync(
            boulderId,
            async () => OfflineActionRunner.Applied(clientRequestId, await write()),
            hideBoulders: true);
    }
}
