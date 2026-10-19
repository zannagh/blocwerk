// <copyright file="WallRefreshApiController.Checks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Refresh;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The confirm screen's cards over the API: list them with their current answers, and answer one or several. Each
/// answer is the page's own (<see cref="IWallRefreshService.DecideChecksAsync"/>): written to the run's update session
/// as the full review's decision, refused when that session was replaced, and the summary is then made again. Apply
/// waits for that summary; read <c>GET …/summary</c> again for the new <c>decisionsVersion</c>.
/// </summary>
public sealed partial class WallRefreshApiController
{
    /// <summary>The cards and how each is answered now (empty unless the run waits for its confirm). Changes nothing.</summary>
    [HttpGet("{refreshId:guid}/checks")]
    public Task<IActionResult> Checks(Guid wallId, Guid refreshId) =>
        ReadAsync(wallId, () => ForRunAsync(wallId, refreshId, async _ => Ok(await refreshes.GetChecksAsync(refreshId))));

    /// <summary>Answers one card. 202 <c>{ answered: 1 }</c>; 409 when it is refused (see the error).</summary>
    [HttpPost("{refreshId:guid}/checks/{checkId:guid}/answer")]
    public Task<IActionResult> AnswerCheck(Guid wallId, Guid refreshId, Guid checkId, [FromBody] RefreshCheckAnswerRequest? body) =>
        body?.Answer is not { } answer
            ? Task.FromResult<IActionResult>(BadRequest(new ApiErrorResponse("Send an answer: keep, remove or undo.")))
            : AnswerChecksAsync(wallId, refreshId, "refresh.check", [new RefreshCheckAnswer(checkId, answer)]);

    /// <summary>
    /// Answers several cards in order; the first refusal stops the rest (409, those before it stay answered).
    /// 202 <c>{ answered: n }</c>.
    /// </summary>
    [HttpPost("{refreshId:guid}/checks/answers")]
    public Task<IActionResult> AnswerChecks(Guid wallId, Guid refreshId, [FromBody] RefreshCheckAnswersRequest? body)
    {
        var answers = body?.Answers ?? [];
        if (answers.Count == 0 || answers.Any(a => a.Answer is null))
        {
            return Task.FromResult<IActionResult>(BadRequest(new ApiErrorResponse("Send answers: [{ checkId, answer }], answer keep, remove or undo.")));
        }

        return AnswerChecksAsync(wallId, refreshId, "refresh.checks", answers.Select(a => new RefreshCheckAnswer(a.CheckId, a.Answer!.Value)).ToList());
    }

    private Task<IActionResult> AnswerChecksAsync(Guid wallId, Guid refreshId, string action, IReadOnlyList<RefreshCheckAnswer> answers) =>
        WriteAsync(wallId, action, () => ForRunAsync(wallId, refreshId, async _ =>
            Accepted(new RefreshCheckAnswered(await refreshes.DecideChecksAsync(refreshId, answers)))));
}
