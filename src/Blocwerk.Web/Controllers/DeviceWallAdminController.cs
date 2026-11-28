// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The wall settings' "iPhone scan" card and danger zone for the companion app, through the web's own service calls:
/// <see cref="IWallUpdateModeService"/> for the mode (admins), <see cref="IWallScanService"/> for the scan list (admins)
/// and the removal of a received scan (administrators of the installation), <see cref="IWallService.DeleteWallAsync"/>
/// for deleting the wall (the owner only, with its name typed again, as the web's confirmation asks).
/// </summary>
/// <remarks>
/// A linked device key only, never cached. A wall the caller is not a member of answers a bare 404; a member without the
/// role gets 403 <c>{ message }</c>. Setting the mode and deleting are absolute, so a retry is harmless.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceWallAdminController : ControllerBase
{
    /// <summary>The refusal when the typed name is not the wall's.</summary>
    public const string ConfirmNameMessage = "confirmName must be the wall's name, exactly.";

    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;
    private readonly IWallUpdateModeService modes;
    private readonly IWallScanService scans;

    public DeviceWallAdminController(
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        IWallUpdateModeService modes,
        IWallScanService scans)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
        this.modes = modes;
        this.scans = scans;
    }

    /// <summary>The wall's update mode, its recent scans and whether the caller may remove them; admins only.</summary>
    [HttpGet("scan-mode")]
    [DeviceKeySyncRateLimit]
    public async Task<IActionResult> GetScanMode(Guid wallId, CancellationToken ct)
    {
        if (DeviceWallRefusal.UnlessAdmin(await CallerAsync(wallId, ct)) is { } refused)
        {
            return refused;
        }

        return await RunAsync(async () => Ok(await ViewAsync(await modes.GetAsync(wallId), wallId, ct)));
    }

    /// <summary>Sets the update mode (Panels or Scan); back to Panels is refused once a scan has been applied.</summary>
    [HttpPut("scan-mode")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> SetScanMode(Guid wallId, [FromBody] SetScanModeRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<WallUpdateMode>(request.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode) || int.TryParse(request.Mode, out _))
        {
            return DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, "mode must be Panels or Scan.");
        }

        if (DeviceWallRefusal.UnlessAdmin(await CallerAsync(wallId, ct)) is { } refused)
        {
            return refused;
        }

        return await RunAsync(async () => Ok(await ViewAsync(await modes.SetAsync(wallId, mode), wallId, ct)));
    }

    /// <summary>Removes a received scan with its files; administrators of the installation only.</summary>
    [HttpDelete("scans/{scanId:guid}")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> DeleteScan(Guid wallId, Guid scanId, CancellationToken ct)
    {
        var caller = await CallerAsync(wallId, ct);
        var siteAdmin = caller is not null && await scans.MayDeleteAsync(ct);
        if (!siteAdmin)
        {
            return caller is { IsMember: true }
                ? DeviceWallRefusal.Refused(StatusCodes.Status403Forbidden, "Only administrators of this installation can remove scans.")
                : NotFound();
        }

        try
        {
            await scans.DeleteAsync(wallId, scanId, ct);
            return NoContent();
        }
        catch (ScanUploadException ex)
        {
            return DeviceWallRefusal.Refused(ex.StatusCode, ex.Message);
        }
    }

    /// <summary>Deletes the wall for good: the owner only, with <c>confirmName</c> equal to the wall's name.</summary>
    [HttpDelete]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> DeleteWall(Guid wallId, [FromBody] DeleteWallRequest request, [FromServices] IWallService walls, CancellationToken ct)
    {
        var caller = await CallerAsync(wallId, ct);
        if (DeviceWallRefusal.UnlessOwner(caller) is { } refused)
        {
            return refused;
        }

        if (request.ConfirmName is null || !string.Equals(request.ConfirmName, await NameAsync(wallId, ct), StringComparison.Ordinal))
        {
            return DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, ConfirmNameMessage);
        }

        return await RunAsync(async () =>
        {
            await walls.DeleteWallAsync(wallId);
            return NoContent();
        });
    }

    private async Task<DeviceScanModeResponse> ViewAsync(WallUpdateModeView view, Guid wallId, CancellationToken ct) =>
        new(view.Mode.ToString(), view.CanSwitchToPanels, await scans.ListAsync(wallId, ct), await scans.MayDeleteAsync(ct));

    private async Task<string?> NameAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Walls.IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == wallId).Select(w => w.Name).FirstOrDefaultAsync(ct);
    }

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

    private static async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is UserFacingException { Message: WallUpdateModeService.LockedMessage })
        {
            return DeviceWallRefusal.Refused(StatusCodes.Status409Conflict, ex.Message);
        }
        catch (Exception ex) when (DeviceWallRefusal.FromService(ex) is { } refusal)
        {
            return refusal;
        }
    }
}
