// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>A wall's settings as the web wall settings card shows them, for the companion app.</summary>
/// <param name="Name">The wall's name.</param>
/// <param name="Description">Its description, or null.</param>
/// <param name="Angle">The overhang angle in degrees.</param>
/// <param name="UnderMaintenance">True while the wall is in update mode.</param>
/// <param name="MyRole">The caller's role: Member, Moderator or Admin (the owner is Admin).</param>
/// <param name="OwnerId">The wall's owner.</param>
/// <param name="PhotoSha">The wall photo's sha (read it as <c>/api/walls/{w}/a/{sha}</c>), or null.</param>
/// <param name="PanelCount">The live panels of the wall.</param>
/// <param name="AllowAnonymousKiosk">True when the wall's kiosk may set boulders without anybody picked.</param>
/// <param name="AllowKioskKeyboardShortcuts">True when the wall's kiosk takes keyboard shortcuts.</param>
public sealed record DeviceWallSettingsResponse(
    string Name,
    string? Description,
    int Angle,
    bool UnderMaintenance,
    string MyRole,
    Guid OwnerId,
    string? PhotoSha,
    int PanelCount,
    bool AllowAnonymousKiosk,
    bool AllowKioskKeyboardShortcuts);
