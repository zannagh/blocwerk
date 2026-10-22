// <copyright file="MaintenanceFlushController.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The autodeploy hook's "save what you can, I am about to recreate you": every circuit with an open editor persists
/// its unsaved state (<see cref="EditFlushHub"/>) before a deploy that goes ahead while editing leases still exist.
/// </summary>
/// <remarks>
/// Same guard as <see cref="MaintenanceAnnouncementController"/>: Installation-scoped API key only, scheme pinned so no
/// browser cookie can reach it, under <c>/api/v1</c> because that is where a <c>bwk_</c> key is honoured. It waits at
/// most <see cref="EditPresenceOptions.FlushTimeout"/>, and it answers 200 with the report even when some circuits
/// timed out: the deploy is going ahead either way, and the report is what gets logged.
/// </remarks>
[ApiController]
[Route("api/v1/maintenance")]
[Authorize(Policy = BlocwerkPolicies.InstallationApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class MaintenanceFlushController(
    EditFlushHub hub,
    IOptions<EditPresenceOptions> options,
    ILogger<MaintenanceFlushController> logger) : ControllerBase
{
    /// <summary>Asks every open editor to save, waits for them (bounded), and reports what happened.</summary>
    /// <remarks>Antiforgery is not applicable: the caller is a machine holding a bearer key.</remarks>
    [HttpPost("flush-edits")]
    public async Task<ActionResult<EditFlushReport>> FlushEdits(CancellationToken ct)
    {
        logger.LogInformation(
            "Pre-deploy flush requested by API key {ApiKeyId} for {Sessions} circuit(s)",
            User.FindFirst(ApiKeyClaimTypes.ApiKeyId)?.Value ?? "unknown",
            hub.Count);
        return Ok(await hub.FlushAsync(options.Value.FlushTimeout, ct));
    }
}
