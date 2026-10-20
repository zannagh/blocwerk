// <copyright file="WallRefreshApiController.Review.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Refresh.Review;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The one review over the API (<see cref="IUpdateReviewService"/>): every hold to check before Apply, the page's own
/// answers, and "accept all suggested" per group. Each answer is written to the run's update session as the decision
/// Apply reads; the summary is then made again in the background (read <c>GET …/summary</c> for the new
/// <c>decisionsVersion</c> before Apply). The checks routes keep working: cards are items with a <c>c-</c> key here.
/// </summary>
public sealed partial class WallRefreshApiController
{
    /// <summary>The review: counts, panels and items (404 unless the run waits for its Apply). Changes nothing.</summary>
    [HttpGet("{refreshId:guid}/review")]
    public Task<IActionResult> Review(Guid wallId, Guid refreshId, [FromServices] IUpdateReviewService review) =>
        ReadAsync(wallId, () => ForRunAsync(wallId, refreshId, async _ =>
            await review.GetAsync(refreshId) is { } shown
                ? new JsonResult(shown, ReviewJson.Options)
                : NotFound(new ApiErrorResponse("The update is not waiting for its review."))));

    /// <summary>Answers items in order; the first refusal stops the rest (202 when some were written, else 409).</summary>
    [HttpPost("{refreshId:guid}/review/answers")]
    public Task<IActionResult> AnswerReview(
        Guid wallId, Guid refreshId, [FromBody] ReviewAnswersRequest? body, [FromServices] IUpdateReviewService review)
    {
        var answers = body?.Answers ?? [];
        if (answers.Count == 0 || answers.Count > UpdateReviewService.MaxAnswers || answers.Any(a => a.Answer is null || string.IsNullOrEmpty(a.Key)))
        {
            return Task.FromResult<IActionResult>(BadRequest(new ApiErrorResponse(
                $"Send 1 to {UpdateReviewService.MaxAnswers} answers: [{{ key, answer }}] with an item's key and one of its answers.")));
        }

        var requests = answers.Select(a => new ReviewAnswerRequest(a.Key!, a.Answer!.Value)).ToList();
        return WriteAsync(wallId, "refresh.review", () => ForRunAsync(wallId, refreshId, async _ =>
        {
            try
            {
                var after = await review.AnswerAsync(refreshId, requests);
                return Answered(new ReviewAnswered(requests.Count, true, after.Counts), accepted: true);
            }
            catch (ReviewAnswersStoppedException ex)
            {
                return Answered(new ReviewAnswered(ex.Answered, false, null, ex.FailedKey, ex.Message), accepted: ex.Answered > 0);
            }
        }));
    }

    /// <summary>Answers every unanswered item of a group (of one panel, when given) with its suggested answer. 202.</summary>
    [HttpPost("{refreshId:guid}/review/accept")]
    public Task<IActionResult> AcceptReviewGroup(
        Guid wallId, Guid refreshId, [FromBody] ReviewAcceptRequest? body, [FromServices] IUpdateReviewService review) =>
        body?.Group is not { } group
            ? Task.FromResult<IActionResult>(BadRequest(new ApiErrorResponse("Send a group: unsure, handPlaced, replaced, notFound, leftOut, newHold, …")))
            : WriteAsync(wallId, "refresh.review.accept", () => ForRunAsync(wallId, refreshId, async _ =>
            {
                var (answered, after) = await review.AcceptSuggestedAsync(refreshId, group, body.PanelId);
                return Answered(new ReviewAnswered(answered, true, after.Counts), accepted: true);
            }));

    private static JsonResult Answered(ReviewAnswered body, bool accepted) =>
        new(body, ReviewJson.Options) { StatusCode = accepted ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict };
}
