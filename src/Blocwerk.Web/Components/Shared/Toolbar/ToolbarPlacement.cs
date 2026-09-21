// <copyright file="ToolbarPlacement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.Toolbar;

/// <summary>Where a <see cref="UnifiedToolbar"/> sits relative to the surface it drives.</summary>
public enum ToolbarPlacement
{
    /// <summary>A horizontal bar above the image. The default, and the only shape that fits a phone.</summary>
    TopBar,

    /// <summary>A vertical column to the right of the image, for the desktop two-column layout.</summary>
    Sidebar,
}
