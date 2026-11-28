// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The wall's first photo for the companion app: the web's empty-wall upload (<see cref="IWallService.UploadPhotoAsync"/>,
/// which strips the metadata, makes the centre panel and detects the holds), with the gallery's hardening
/// (<see cref="WallPhotoIntake"/>). Like the web, only on a wall that has no photo and no panels yet; a wall with photos
/// is updated through the wall update.
/// </summary>
/// <remarks>
/// A linked device key only, write-limited, never cached; admins (the owner included) only. Every upload detects holds
/// anew, so it is not idempotent: <c>clientRequestId</c> is required (query string), and a retry answers the stored
/// photo's sha instead of uploading again.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}/photo")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeyWriteRateLimit]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceWallPhotoController : ControllerBase
{
    /// <summary>The receipt kind of a wall photo upload.</summary>
    public const string ReceiptKind = "wall.photo";

    /// <summary>The refusal on a wall that already has its photo or panels.</summary>
    public const string HasPhotoMessage = "This wall already has a photo or panels. Update it through the wall update.";

    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;
    private readonly IWallService walls;
    private readonly IAssetHashReader hashes;
    private readonly DeviceWriteReceiptService receipts;

    public DeviceWallPhotoController(
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        IWallService walls,
        IAssetHashReader hashes,
        DeviceWriteReceiptService receipts)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
        this.walls = walls;
        this.hashes = hashes;
        this.receipts = receipts;
    }

    /// <summary>Uploads the photo as the raw body (image/jpeg, image/png or image/heic).</summary>
    [HttpPut]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Upload(
        Guid wallId,
        [FromQuery] Guid? clientRequestId,
        [FromServices] IEnumerable<ICapturePhotoConverter> converters,
        CancellationToken ct)
    {
        if (clientRequestId is null || clientRequestId == Guid.Empty)
        {
            return DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, "clientRequestId is required.");
        }

        var caller = await CallerAsync(wallId, ct);
        if (DeviceWallRefusal.UnlessAdmin(caller) is { } refused)
        {
            return refused;
        }

        var claim = await receipts.ClaimAsync(caller!.UserId, clientRequestId, ReceiptKind, wallId, ct);
        switch (claim.State)
        {
            case DeviceWriteClaimState.Replay:
                return Ok(new DeviceWallPhotoResponse(await ShaAsync(wallId, ct)));
            case DeviceWriteClaimState.InFlight:
                return DeviceWallRefusal.Refused(StatusCodes.Status409Conflict, "The same upload is still running. Retry shortly.");
            case DeviceWriteClaimState.Mismatch:
                return DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, "clientRequestId was already used for another write.");
        }

        var result = await StoreAsync(wallId, converters.FirstOrDefault(), ct);
        if (result is OkObjectResult)
        {
            await receipts.CompleteAsync(caller.UserId, clientRequestId, wallId, ct);
        }
        else
        {
            await receipts.ReleaseAsync(caller.UserId, clientRequestId);
        }

        return result;
    }

    private async Task<IActionResult> StoreAsync(Guid wallId, ICapturePhotoConverter? converter, CancellationToken ct)
    {
        if (await HasPhotoOrPanelsAsync(wallId, ct))
        {
            return DeviceWallRefusal.Refused(StatusCodes.Status409Conflict, HasPhotoMessage);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DeviceWriteReceiptService.RequestTimeout);
        var (bytes, type, status, error) = await WallPhotoIntake.ReadAsync(Request, converter, deadline.Token);
        if (bytes is null)
        {
            return DeviceWallRefusal.Refused(status, error!);
        }

        try
        {
            await walls.UploadPhotoAsync(wallId, bytes, type!);
        }
        catch (Exception ex) when (DeviceWallRefusal.FromService(ex) is { } refusal)
        {
            return refusal;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Device wall photo upload failed for wall {WallId}", wallId);
            return DeviceWallRefusal.Refused(StatusCodes.Status500InternalServerError, "The photo could not be stored.");
        }

        return Ok(new DeviceWallPhotoResponse(await ShaAsync(wallId, ct)));
    }

    private async Task<bool> HasPhotoOrPanelsAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(w => w.Id == wallId && (w.PhotoContentType != null || db.WallPanels.Any(p => p.WallId == wallId)), ct);
    }

    private async Task<string?> ShaAsync(Guid wallId, CancellationToken ct) =>
        (await hashes.WallPhotosAsync([wallId], ct)).GetValueOrDefault(wallId);

    private async Task<WallCaller?> CallerAsync(Guid wallId, CancellationToken ct)
    {
        try
        {
            return await WallCaller.ReadAsync(contexts, currentUser, wallId, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
