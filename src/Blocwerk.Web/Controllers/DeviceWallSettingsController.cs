// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The wall settings card and member roles for the companion app, through the web's own service calls
/// (<see cref="IWallService.UpdateWallAsync"/>, <see cref="IWallService.SetMaintenanceAsync"/>,
/// <see cref="IWallService.SetAnonymousKioskSettingAsync"/>, <see cref="IWallService.SetKioskKeyboardShortcutsAsync"/>,
/// <see cref="IWallService.SetMemberRoleAsync"/>), so the web's rules hold: members read the settings, admins (the
/// owner included) change them and the roles, and nobody changes the owner's role.
/// </summary>
/// <remarks>
/// A linked device key only, never cached. A wall the caller is not a member of answers a bare 404; a member who is not
/// an admin gets 403. Refusals are <c>{ message }</c>. Every write sets absolute values, so a retry is harmless.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceWallSettingsController : ControllerBase
{
    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;
    private readonly IWallService walls;
    private readonly DeviceWallSettingsReader reader;

    public DeviceWallSettingsController(
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        IWallService walls,
        DeviceWallSettingsReader reader)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
        this.walls = walls;
        this.reader = reader;
    }

    /// <summary>The wall's settings and the caller's role.</summary>
    [HttpGet("settings")]
    [DeviceKeySyncRateLimit]
    public async Task<IActionResult> GetSettings(Guid wallId, CancellationToken ct)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true } caller)
        {
            return NotFound();
        }

        return await reader.ReadAsync(wallId, caller, ct) is { } settings ? Ok(settings) : NotFound();
    }

    /// <summary>Changes any of name, description, angle, update mode and the two kiosk switches; admins only.</summary>
    [HttpPatch("settings")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> PatchSettings(Guid wallId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (patch, error) = WallSettingsPatch.Parse(body);
        if (patch is null)
        {
            return BadRequest(new ApiErrorResponse(error!));
        }

        var caller = await CallerAsync(wallId, ct);
        if (DeviceWallRefusal.UnlessAdmin(caller) is { } refused)
        {
            return refused;
        }

        var current = await reader.ReadAsync(wallId, caller!, ct);
        if (current is null)
        {
            return NotFound();
        }

        return await RunAsync(async () =>
        {
            await ApplyAsync(wallId, patch, current);
            return Ok(await reader.ReadAsync(wallId, caller!, ct));
        });
    }

    /// <summary>Sets a member's role (Member, Moderator or Admin); admins only, never the owner's.</summary>
    [HttpPut("members/{userId:guid}/role")]
    [DeviceKeyWriteRateLimit]
    public async Task<IActionResult> SetRole(Guid wallId, Guid userId, [FromBody] SetMemberRoleRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<WallRole>(request.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role) || int.TryParse(request.Role, out _))
        {
            return BadRequest(new ApiErrorResponse("role must be Member, Moderator or Admin."));
        }

        if (DeviceWallRefusal.UnlessAdmin(await CallerAsync(wallId, ct)) is { } refused)
        {
            return refused;
        }

        return await RunAsync(async () =>
        {
            await walls.SetMemberRoleAsync(wallId, userId, role);
            return Ok(new SetMemberRoleResponse(userId, role.ToString()));
        });
    }

    private async Task ApplyAsync(Guid wallId, WallSettingsPatch patch, DeviceWallSettingsResponse current)
    {
        if (patch.ChangesDetails)
        {
            var description = patch.Description is null ? current.Description : (patch.Description.Length == 0 ? null : patch.Description);
            await walls.UpdateWallAsync(wallId, patch.Name ?? current.Name, description, patch.Angle);
        }

        if (patch.UnderMaintenance is { } maintenance)
        {
            await walls.SetMaintenanceAsync(wallId, maintenance);
        }

        if (patch.AllowAnonymousKiosk is { } anonymous)
        {
            await walls.SetAnonymousKioskSettingAsync(wallId, anonymous);
        }

        if (patch.AllowKioskKeyboardShortcuts is { } shortcuts)
        {
            await walls.SetKioskKeyboardShortcutsAsync(wallId, shortcuts);
        }
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

    private static async Task<IActionResult> RunAsync(Func<Task<IActionResult>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception ex) when (DeviceWallRefusal.FromService(ex) is { } refusal)
        {
            return refusal;
        }
    }
}
