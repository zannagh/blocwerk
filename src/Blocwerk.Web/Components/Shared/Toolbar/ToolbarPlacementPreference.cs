// <copyright file="ToolbarPlacementPreference.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.Toolbar;

/// <summary>
/// The member's toolbar-placement preference, stored as a plain cookie by <c>wwwroot/js/prefs.js</c>
/// (<c>bwPrefs.getToolbarPlacement</c> / <c>setToolbarPlacement</c>).
/// <para>
/// A cookie rather than localStorage for the same reason as the zoom-lens magnification: the server
/// can read it on the prerender pass, so the editor's FIRST paint is already in the right shape and
/// there is no flash of the wrong placement. This type is the one place the cookie name and its two
/// values are written on the C# side — keep it in step with prefs.js.
/// </para>
/// </summary>
public static class ToolbarPlacementPreference
{
    /// <summary>The cookie prefs.js writes. Also listed on the privacy page.</summary>
    public const string CookieName = "blocwerk-toolbar-placement";

    /// <summary>Cookie value for <see cref="ToolbarPlacement.Sidebar"/>.</summary>
    public const string SidebarValue = "sidebar";

    /// <summary>Cookie value for <see cref="ToolbarPlacement.TopBar"/>, and the default.</summary>
    public const string TopBarValue = "topbar";

    /// <summary>
    /// Reads a stored value. Unset, misspelled or tampered-with content falls back to the top bar,
    /// which is the placement that fits every screen.
    /// </summary>
    public static ToolbarPlacement Parse(string? stored)
    {
        if (string.Equals(stored, SidebarValue, StringComparison.OrdinalIgnoreCase))
        {
            return ToolbarPlacement.Sidebar;
        }

        return ToolbarPlacement.TopBar;
    }

    /// <summary>The cookie value for a placement.</summary>
    public static string ToCookieValue(ToolbarPlacement placement) =>
        placement == ToolbarPlacement.Sidebar ? SidebarValue : TopBarValue;
}
