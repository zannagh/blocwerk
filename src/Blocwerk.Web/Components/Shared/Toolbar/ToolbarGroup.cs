// <copyright file="ToolbarGroup.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.Toolbar;

/// <summary>
/// A run of related controls. Groups are what the separators between controls mean, so the host
/// expresses the grouping instead of sprinkling <c>&lt;span class="toolbar-sep"&gt;</c> by hand — and a
/// group whose every item is hidden draws no separator at all.
/// </summary>
public sealed record ToolbarGroup
{
    /// <summary>Stable id, used as the render key.</summary>
    public required string Id { get; init; }

    /// <summary>The controls in this group, in order.</summary>
    public required IReadOnlyList<ToolbarItem> Items { get; init; }

    /// <summary>The items currently shown.</summary>
    public IEnumerable<ToolbarItem> VisibleItems => Items.Where(i => i.IsVisible);
}
