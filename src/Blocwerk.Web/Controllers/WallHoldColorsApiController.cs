// <copyright file="WallHoldColorsApiController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// "Suggest colours for holds without one" over the machine API: the wall page's action
/// (<see cref="IHoldColorProposalService"/>), so a rollout script can run it after a panel update. Safe to repeat.
/// </summary>
/// <remarks>
/// The same key as "Update panels + 3D" (<see cref="WallRefreshApiController"/>): a personal key with write access whose
/// owner is an admin of the wall (the service's check; kiosks are refused there too). The write is audited in the change
/// journal (<see cref="ApiWriteAudit"/>). Antiforgery does not apply, for the reason given there.
/// </remarks>
[ApiController]
[Route("api/walls/{wallId:guid}/holds/suggest-colors")]
[Authorize(Policy = BlocwerkPolicies.HumanOrUserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed class WallHoldColorsApiController(
    IHoldColorProposalService proposals,
    ApiWriteAudit audit,
    ILogger<WallHoldColorsApiController> logger) : WallAdminApiController(logger)
{
    /// <summary>Proposes a colour for every live hold of the wall that has none; holds with a colour are not touched.</summary>
    [HttpPost]
    public Task<IActionResult> Suggest(Guid wallId) =>
        User.IsWritablePersonalKey()
            ? RunAsync(wallId, () => audit.RunAsync(User, wallId, "holds.suggest-colors", async () =>
                Ok(HoldColorSuggestionResponse.From(await proposals.SuggestMissingAsync(wallId)))))
            : Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(WallRefreshApiController.PersonalKeyOnly)));
}
