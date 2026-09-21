// <copyright file="ToolbarSpec.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.Toolbar;

/// <summary>
/// What a host wants on its toolbar. One component renders every spec, so the wall editor, the
/// touch-up surfaces and the hold picker stop being three visual languages over three state models.
/// </summary>
public sealed record ToolbarSpec
{
    /// <summary>The separator-delimited groups of controls.</summary>
    public required IReadOnlyList<ToolbarGroup> Groups { get; init; }

    /// <summary>Status text at the leading edge of the bar — the editor's hold counts.</summary>
    public RenderFragment? Status { get; init; }

    /// <summary>
    /// What the contextual row shows when nothing with a row is active. It still occupies the row's
    /// full height, because the row's height is what keeps the image from moving.
    /// </summary>
    public RenderFragment? IdleRow { get; init; }

    /// <summary>Every visible item, flattened.</summary>
    public IEnumerable<ToolbarItem> VisibleItems => Groups.SelectMany(g => g.VisibleItems);

    /// <summary>
    /// The row to show for <paramref name="tool"/>: an on Toggle's row wins over the picked Tool's,
    /// because a toggle that has a row (hold usage) parks the tool while it is open.
    /// </summary>
    public RenderFragment? RowFor(HoldTouchupTool tool)
    {
        foreach (var item in VisibleItems)
        {
            if (item.Kind == ToolbarItemKind.Toggle && item.Row is not null && (item.IsOn?.Invoke() ?? false))
            {
                return item.Row;
            }
        }

        foreach (var item in VisibleItems)
        {
            if (item.Kind == ToolbarItemKind.Tool && item.Tool == tool && item.Row is not null)
            {
                return item.Row;
            }
        }

        return IdleRow;
    }
}
