// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services;

/// <summary>
/// A change to a wall's settings card, applied in one save by <see cref="IWallService.UpdateSettingsAsync"/>. A null
/// field stays as it is; <see cref="Description"/> <c>""</c> clears the description.
/// </summary>
/// <param name="Name">The new name, or null.</param>
/// <param name="Description">The new description ("" clears it), or null.</param>
/// <param name="Angle">The overhang angle, or null.</param>
/// <param name="UnderMaintenance">Update mode on or off, or null.</param>
/// <param name="AllowAnonymousKiosk">Anonymous kiosk setting on or off, or null.</param>
/// <param name="AllowKioskKeyboardShortcuts">Kiosk keyboard shortcuts on or off, or null.</param>
public sealed record WallSettingsUpdate(
    string? Name = null,
    string? Description = null,
    int? Angle = null,
    bool? UnderMaintenance = null,
    bool? AllowAnonymousKiosk = null,
    bool? AllowKioskKeyboardShortcuts = null)
{
    /// <summary>True when the update touches a kiosk permission, which a kiosk session may never change.</summary>
    public bool ChangesKiosk => AllowAnonymousKiosk is not null || AllowKioskKeyboardShortcuts is not null;
}
