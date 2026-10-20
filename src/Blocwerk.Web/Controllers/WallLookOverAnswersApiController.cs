// <copyright file="WallLookOverAnswersApiController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Answering the wall look-over over the machine API: one finding, taking an answer back, and finishing. Every call is
/// the page's own <see cref="IWallLookOverService"/> call, so the same rules hold — an answer only reaches a live hold,
/// and only with the boulders the caller was shown (<c>expectedBoulderIds</c>, from the finding's <c>boulders</c>). The
/// reads stay on <see cref="WallLookOverApiController"/>.
/// </summary>
/// <remarks>
/// The same key as the reads: a personal key with write access whose owner may look over the wall (the service
/// decides). Writes are audited in the change journal (<see cref="ApiWriteAudit"/>); the answers' own changes land in
/// the look-over's own batch, as from the page. "It is this hold" (<c>Reassign</c> with <c>targetHoldId</c>), "It is
/// here" (<c>Moved</c> with a <c>spot</c>) and "Different hold, this one replaced it" (<c>DifferentHold</c> with
/// <c>targetHoldId</c>) are the page's own calls too, with its checks (<see cref="LookOverAnswerShape"/>).
/// </remarks>
[ApiController]
[Route("api/walls/{wallId:guid}/look-over")]
[Authorize(Policy = BlocwerkPolicies.HumanOrUserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed class WallLookOverAnswersApiController(
    IWallLookOverService lookOver,
    ApiWriteAudit audit,
    ILogger<WallLookOverAnswersApiController> logger) : WallAdminApiController(logger)
{
    /// <summary>Answers one open finding. 200 with the look-over's state; 409 when it is refused (see the error).</summary>
    [HttpPost("findings/{findingId:guid}/answer")]
    public Task<IActionResult> Answer(Guid wallId, Guid findingId, [FromBody] LookOverAnswerRequest? body)
    {
        if (body?.Answer is not { } answer)
        {
            return Task.FromResult<IActionResult>(BadRequest(new ApiErrorResponse("Send an answer the finding takes (its answers).")));
        }

        if ((NewHoldProblem(answer, body) ?? LookOverAnswerShape.Problem(answer, body)) is { } problem)
        {
            return Task.FromResult<IActionResult>(BadRequest(new ApiErrorResponse(problem)));
        }

        return WriteAsync(wallId, "lookover.answer", async () =>
        {
            await LookOverAnswerShape.RunAsync(lookOver, wallId, findingId, answer, body);
            return Ok(await lookOver.GetStateAsync(wallId));
        });
    }

    /// <summary>Takes back an answer that changed nothing on the wall; the finding is open again. 200 with the state.</summary>
    [HttpPost("findings/{findingId:guid}/undo")]
    public Task<IActionResult> Undo(Guid wallId, Guid findingId) =>
        WriteAsync(wallId, "lookover.undo", async () =>
        {
            await lookOver.UndoAsync(wallId, findingId);
            return Ok(await lookOver.GetStateAsync(wallId));
        });

    /// <summary>
    /// Finishes the look-over (the flag comes off the wall). 409 while findings are open, unless <c>leaveOpen</c> is true.
    /// 200 with the state.
    /// </summary>
    [HttpPost("finish")]
    public Task<IActionResult> Finish(Guid wallId, [FromBody] LookOverFinishRequest? body) =>
        WriteAsync(wallId, "lookover.finish", async () =>
        {
            await lookOver.FinishAsync(wallId, body?.LeaveOpen ?? false);
            return Ok(await lookOver.GetStateAsync(wallId));
        });

    /// <summary>What is wrong with a per-boulder "use the new hold", before the service is asked; null when nothing is.</summary>
    private static string? NewHoldProblem(LookOverAnswer answer, LookOverAnswerRequest body)
    {
        if (body.NewHoldBoulderIds is not { } chosen)
        {
            return null;
        }

        if (answer != LookOverAnswer.UseNewHold)
        {
            return "newHoldBoulderIds goes only with the answer UseNewHold.";
        }

        return chosen.All(id => body.ExpectedBoulderIds?.Contains(id) == true)
            ? null
            : "newHoldBoulderIds must be among expectedBoulderIds (the finding's boulders as read).";
    }

    private Task<IActionResult> WriteAsync(Guid wallId, string action, Func<Task<IActionResult>> write) =>
        User.IsWritablePersonalKey()
            ? RunAsync(wallId, () => audit.RunAsync(User, wallId, action, write))
            : Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(WallRefreshApiController.PersonalKeyOnly)));
}
