// <copyright file="WallRefreshApiController.Apply.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>The confirm: apply the summary the caller checked, and nothing else.</summary>
public sealed partial class WallRefreshApiController
{
    /// <summary>
    /// Applies the prepared panel update, but only with the <c>decisionsVersion</c> of the summary the caller checked:
    /// 400 without one, 409 with the run's current version when it changed since (or there is nothing to apply, or the
    /// 3D check is still running). 202 when queued; poll the run until it is Done. Should the decisions change between
    /// this call and the background apply, nothing is promoted and the run is back at ReadyToApply with a new version.
    /// </summary>
    [HttpPost("{refreshId:guid}/apply")]
    public Task<IActionResult> Apply(Guid wallId, Guid refreshId, [FromBody] RefreshApplyRequest? body) =>
        WriteAsync(wallId, "refresh.apply", () => ForRunAsync(wallId, refreshId, async _ =>
        {
            if (string.IsNullOrWhiteSpace(body?.DecisionsVersion))
            {
                return BadRequest(new ApiErrorResponse("Send the decisionsVersion of the summary you checked (GET …/summary)."));
            }

            try
            {
                await refreshes.ApplyAsync(refreshId, body.DecisionsVersion);
            }
            catch (UserFacingException ex)
            {
                var now = await refreshes.GetCurrentAsync(wallId);
                return Conflict(new RefreshApplyRefused(ex.Message, now?.Status, now?.Summary?.DecisionsVersion));
            }

            return Accepted(new RefreshApplyAccepted(refreshId, body.DecisionsVersion));
        }));
}
