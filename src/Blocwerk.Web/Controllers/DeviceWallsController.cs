// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// "+ New Wall" for the companion app: the web create page's own call (<see cref="IWallService.CreateWallAsync"/>), which
/// makes the caller the wall's owner and admin. The photo follows with <c>PUT /api/v1/walls/{w}/photo</c>, as the web
/// uploads it right after creating.
/// </summary>
/// <remarks>
/// A linked device key only, write-limited, never cached. A create is not idempotent, so <c>clientRequestId</c> is
/// required: a retry answers the wall the first request made instead of making a second one. Refusals are
/// <c>{ message }</c>.
/// </remarks>
[ApiController]
[Route("api/v1/walls")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeyWriteRateLimit]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceWallsController : ControllerBase
{
    /// <summary>The receipt kind of a wall create.</summary>
    public const string ReceiptKind = "wall.create";

    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;
    private readonly IWallService walls;
    private readonly DeviceWriteReceiptService receipts;
    private readonly DeviceKeyWallCreateRateLimiter createLimiter;

    public DeviceWallsController(
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        IWallService walls,
        DeviceWriteReceiptService receipts,
        DeviceKeyWallCreateRateLimiter createLimiter)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
        this.walls = walls;
        this.receipts = receipts;
        this.createLimiter = createLimiter;
    }

    /// <summary>Creates a wall owned by the caller.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeviceCreateWallRequest request, CancellationToken ct)
    {
        if (Error(request) is { } error)
        {
            return DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, error);
        }

        Guid userId;
        try
        {
            userId = (await currentUser.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }

        var claim = await receipts.ClaimAsync(userId, request.ClientRequestId, ReceiptKind, Guid.Empty, ct);
        switch (claim.State)
        {
            case DeviceWriteClaimState.Replay:
                return await CreatedAsync(claim.SubjectId!.Value, userId, ct);
            case DeviceWriteClaimState.InFlight:
                return DeviceWallRefusal.Refused(StatusCodes.Status409Conflict, "The same wall is still being created. Retry shortly.");
            case DeviceWriteClaimState.Mismatch:
                return DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, "clientRequestId was already used for another write.");
        }

        // Only a fresh create spends the key's wall bucket; a replay above never does.
        if (User.GetApiKeyId() is { } keyId && !createLimiter.TryAcquire(keyId))
        {
            await receipts.ReleaseAsync(userId, request.ClientRequestId);
            Response.Headers.RetryAfter = createLimiter.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return DeviceWallRefusal.Refused(StatusCodes.Status429TooManyRequests, "Too many new walls from this device. Try again later.");
        }

        return await CreateAsync(request, userId);
    }

    // The create itself. Once the wall exists its receipt is completed whatever happens to the request (a cancelled
    // request must not free the key, or its retry would make a second wall); only a failed create releases it.
    private async Task<IActionResult> CreateAsync(DeviceCreateWallRequest request, Guid userId)
    {
        Wall wall;
        try
        {
            var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            wall = await walls.CreateWallAsync(request.Name!.Trim(), description, request.Angle ?? 0);
        }
        catch (Exception ex)
        {
            await receipts.ReleaseAsync(userId, request.ClientRequestId);
            if (ex is UserFacingException { Message: WallService.WallLimitMessage })
            {
                return DeviceWallRefusal.Refused(StatusCodes.Status409Conflict, ex.Message);
            }

            if (DeviceWallRefusal.FromService(ex) is { } refusal)
            {
                return refusal;
            }

            throw;
        }

        await receipts.CompleteAsync(userId, request.ClientRequestId, wall.Id, CancellationToken.None);
        return Ok(new DeviceCreatedWall(wall.Id, wall.Name, wall.Description, wall.Angle));
    }

    private static string? Error(DeviceCreateWallRequest request) => request switch
    {
        { ClientRequestId: null } => "clientRequestId is required.",
        { ClientRequestId: var id } when id == Guid.Empty => ClientRequestIds.EmptyMessage,
        _ when string.IsNullOrWhiteSpace(request.Name) => "name is required.",
        _ when request.Name!.Trim().Length > WallSettingsPatch.MaxNameLength => $"name must be at most {WallSettingsPatch.MaxNameLength} characters.",
        _ when request.Description?.Trim().Length > WallSettingsPatch.MaxDescriptionLength =>
            $"description must be at most {WallSettingsPatch.MaxDescriptionLength} characters.",
        _ when request.Angle is < 0 or > 90 => "angle must be from 0 to 90.",
        _ => null,
    };

    // The wall a replayed create made, through the membership filter: a wall deleted since, or one the caller no longer
    // belongs to, is a 404.
    private async Task<IActionResult> CreatedAsync(Guid wallId, Guid userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = userId;
        var wall = await db.Walls.AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new DeviceCreatedWall(w.Id, w.Name, w.Description, w.Angle))
            .FirstOrDefaultAsync(ct);
        return wall is null ? NotFound() : Ok(wall);
    }
}
