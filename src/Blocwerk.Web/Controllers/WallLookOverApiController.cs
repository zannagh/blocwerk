// <copyright file="WallLookOverApiController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.LookOver;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The wall look-over over the machine API, the reads: whether an applied panel update left the wall waiting for a
/// look-over, since when, and the findings. So the script that applied an update can report what it left behind.
/// Answering and finishing are <see cref="WallLookOverAnswersApiController"/>.
/// </summary>
/// <remarks>
/// The same key as "Update panels + 3D" (<see cref="WallRefreshApiController"/>): a personal key with write access; its
/// owner must be allowed to look over the wall (owner, wall admin, moderator, installation admin), which the service
/// decides.
/// </remarks>
[ApiController]
[Route("api/walls/{wallId:guid}/look-over")]
[Authorize(Policy = BlocwerkPolicies.HumanOrUserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallLookOverApiController(
    IWallLookOverService lookOver,
    ILogger<WallLookOverApiController> logger) : WallAdminApiController(logger)
{
    /// <summary>The wall's look-over flag: pending or not, since when, and how many findings are open.</summary>
    [HttpGet]
    public Task<IActionResult> State(Guid wallId) =>
        ReadAsync(wallId, async () => Ok(await StateAsync(wallId)));

    /// <summary>The findings: everything open, and what was answered since the last finished look-over.</summary>
    [HttpGet("findings")]
    public Task<IActionResult> Findings(Guid wallId) =>
        ReadAsync(wallId, async () => Ok(new LookOverFindingsResponse(await StateAsync(wallId), await lookOver.ListAsync(wallId))));

    /// <summary>The state, for somebody who may look over the wall; anybody else is refused like a missing right.</summary>
    private async Task<WallLookOverState> StateAsync(Guid wallId) =>
        await lookOver.GetStateAsync(wallId) is { CanLookOver: true } state
            ? state
            : throw new UnauthorizedAccessException($"The key's owner may not look over wall {wallId}.");

    private Task<IActionResult> ReadAsync(Guid wallId, Func<Task<IActionResult>> read) =>
        User.IsWritablePersonalKey()
            ? RunAsync(wallId, read)
            : Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(WallRefreshApiController.PersonalKeyOnly)));
}
