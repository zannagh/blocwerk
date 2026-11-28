// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The people and flags of a wall's boulders for the companion app (<see cref="DeviceBoulderMeta"/>): creator and setter
/// ids, beta clips, review state, and whether the caller may edit each. They are served here, to members only, and not
/// written into the <c>boulders</c> document, which by design carries no user id: a share link reads that document too.
/// </summary>
/// <remarks>
/// A linked device key only, members of the wall only (a bare 404 for anyone else), counted in the key's sync bucket,
/// never cached. Every boulder a member sees on the web is listed, drafts and the archive included. The feed's
/// <c>boulder.changed</c> and <c>video.added</c> / <c>video.deleted</c> rows say when to read it again.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}/boulder-meta")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeySyncRateLimit]
[Produces("application/json")]
public sealed class DeviceBoulderMetaController : ControllerBase
{
    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;

    public DeviceBoulderMetaController(IDbContextFactory<BlocwerkDbContext> contexts, ICurrentUserService currentUser)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
    }

    /// <summary>One entry per boulder of the wall, ordered by id.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(Guid wallId, CancellationToken ct)
    {
        WallCaller? caller;
        try
        {
            caller = await WallCaller.ReadAsync(contexts, currentUser, wallId, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }

        if (caller is not { IsMember: true })
        {
            return NotFound();
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = caller.UserId;
        var rows = await db.Boulders.AsNoTracking()
            .Where(b => b.WallId == wallId && db.Walls.Any(w => w.Id == b.WallId))
            .Select(b => new
            {
                b.Id,
                b.CreatedByUserId,
                b.NeedsReview,
                Setters = b.Setters.Select(s => s.UserId).ToList(),
                HasBeta = db.BetaVideos.Any(v => v.BoulderId == b.Id),
            })
            .ToListAsync(ct);

        return Ok(rows
            .OrderBy(r => r.Id)
            .Select(r => new DeviceBoulderMeta(
                r.Id,
                r.CreatedByUserId,
                r.Setters.Order().ToList(),
                r.HasBeta,
                r.NeedsReview,
                r.CreatedByUserId == caller.UserId || r.Setters.Contains(caller.UserId) || caller.IsAdmin))
            .ToList());
    }
}
