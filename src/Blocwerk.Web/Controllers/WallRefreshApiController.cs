// <copyright file="WallRefreshApiController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// "Update panels + 3D" over the machine API, for rollout scripts: open a run, drop the files
/// (<c>POST /api/refreshes/{refreshId}/files</c>, the page's own upload route), sort, start with the proposed photos
/// (the quick review's defaults), read the confirm screen's summary, apply exactly that summary's version, or discard.
/// Every call is the <see cref="IWallRefreshService"/> call the page makes, so the run, its rules and its consistency
/// checks are the page's: Apply promotes only what the summary with the sent version describes.
/// </summary>
/// <remarks>
/// A PERSONAL key with write access only, whose owner must be an admin of the wall (the service's check; kiosks are
/// refused there too). Wall keys stay out even with write access: the file drop never admitted them, and replacing
/// panel photos and promoting a new hold generation is a person's decision, not a device's. Writes are audited in the
/// change journal (<see cref="ApiWriteAudit"/>).
/// </remarks>
[ApiController]
[Route("api/walls/{wallId:guid}/refresh")]
[Authorize(Policy = BlocwerkPolicies.HumanOrUserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed partial class WallRefreshApiController(
    IWallRefreshService refreshes,
    ApiWriteAudit audit,
    ILogger<WallRefreshApiController> logger) : WallAdminApiController(logger)
{
    /// <summary>The refusal for every key but a personal key with write access.</summary>
    public const string PersonalKeyOnly =
        "Updating panels over the API needs a personal API key with write access; wall, kiosk and installation keys cannot.";

    /// <summary>The wall's current run (open, or finished within the last day), or 404. Reads change nothing.</summary>
    [HttpGet]
    public Task<IActionResult> Current(Guid wallId) =>
        ReadAsync(wallId, async () => await refreshes.PeekCurrentAsync(wallId) is { } view
            ? Ok(view)
            : NotFound(new ApiErrorResponse("This wall has no current panel update.")));

    /// <summary>Opens a run, or returns the open one.</summary>
    [HttpPost]
    public Task<IActionResult> Begin(Guid wallId) =>
        WriteAsync(wallId, "refresh.begin", async () => Ok(await refreshes.BeginAsync(wallId)));

    /// <summary>The run, as the page shows it. 404 when it is not the wall's current run.</summary>
    [HttpGet("{refreshId:guid}")]
    public Task<IActionResult> Get(Guid wallId, Guid refreshId) =>
        ReadAsync(wallId, () => ForRunAsync(wallId, refreshId, view => Task.FromResult<IActionResult>(Ok(view))));

    /// <summary>The confirm screen: counts, decisions version and whether Apply would be taken now. Changes nothing.</summary>
    [HttpGet("{refreshId:guid}/summary")]
    public Task<IActionResult> Summary(Guid wallId, Guid refreshId) =>
        ReadAsync(wallId, () => ForRunAsync(wallId, refreshId, view => Task.FromResult<IActionResult>(Ok(RefreshSummaryResponse.From(view)))));

    /// <summary>
    /// Starts the check against this visit's new 3D model when it is due (the page starts it on its own when it shows
    /// the run; reads over the API never do). 202 with <c>{ pending }</c>: true while the check is queued or running.
    /// </summary>
    [HttpPost("{refreshId:guid}/recheck")]
    public Task<IActionResult> Recheck(Guid wallId, Guid refreshId) =>
        WriteAsync(wallId, "refresh.recheck", () => ForRunAsync(wallId, refreshId, async _ =>
            Accepted(new RefreshRecheckResponse(await refreshes.RecheckAsync(refreshId)))));

    /// <summary>Done uploading: the photos are sorted to the panels in the background. 202.</summary>
    [HttpPost("{refreshId:guid}/sort")]
    public Task<IActionResult> Sort(Guid wallId, Guid refreshId) =>
        WriteAsync(wallId, "refresh.sort", () => ForRunAsync(wallId, refreshId, async _ =>
        {
            await refreshes.SortAsync(refreshId);
            return Accepted();
        }));

    /// <summary>
    /// Accepts the quick defaults: starts with the sorter's proposed photos (or <c>choices</c>), and the background run
    /// records the quick review's decisions and stops at the confirm screen. Nothing goes live. 202.
    /// </summary>
    [HttpPost("{refreshId:guid}/start")]
    public Task<IActionResult> Start(Guid wallId, Guid refreshId, [FromBody] RefreshStartRequest? body) =>
        WriteAsync(wallId, "refresh.start", () => ForRunAsync(wallId, refreshId, async _ =>
        {
            await refreshes.StartAsync(refreshId, body?.Choices ?? []);
            return Accepted();
        }));

    /// <summary>Throws the run away (and its prepared panel update). 204.</summary>
    [HttpDelete("{refreshId:guid}")]
    public Task<IActionResult> Discard(Guid wallId, Guid refreshId) =>
        WriteAsync(wallId, "refresh.discard", () => ForRunAsync(wallId, refreshId, async _ =>
        {
            await refreshes.DiscardAsync(refreshId);
            return NoContent();
        }));

    private Task<IActionResult> ReadAsync(Guid wallId, Func<Task<IActionResult>> read) =>
        User.IsWritablePersonalKey() ? RunAsync(wallId, read) : Task.FromResult<IActionResult>(NotPersonal());

    private Task<IActionResult> WriteAsync(Guid wallId, string action, Func<Task<IActionResult>> write) =>
        ReadAsync(wallId, () => audit.RunAsync(User, wallId, action, write));

    /// <summary>Runs <paramref name="action"/> on the run when it is the wall's current one, otherwise 404.</summary>
    private async Task<IActionResult> ForRunAsync(Guid wallId, Guid refreshId, Func<WallRefreshView, Task<IActionResult>> action)
    {
        var view = await refreshes.PeekCurrentAsync(wallId);
        return view is { } run && run.Id == refreshId
            ? await action(run)
            : NotFound(new ApiErrorResponse("That panel update is not this wall's current one."));
    }

    private ObjectResult NotPersonal() => StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(PersonalKeyOnly));
}
