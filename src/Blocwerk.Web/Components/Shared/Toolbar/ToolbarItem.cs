// <copyright file="ToolbarItem.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.Toolbar;

/// <summary>
/// One control in a <see cref="ToolbarSpec"/>. A host describes what it needs — it does not pass the
/// toolbar fifteen booleans, and the toolbar does not know what a wall is.
/// <para>
/// <see cref="Row"/> is the item's contextual second row. It is a fragment the ITEM supplies, so the
/// markup can stay where its state lives (the host component) while the toolbar decides when to show
/// it. That is the whole reason the previous extractions stalled: lifting the button row is easy, but
/// the body under each tool reads the host's own fields, and copying it produces a second state
/// machine that drifts.
/// </para>
/// </summary>
public sealed record ToolbarItem
{
    /// <summary>Stable id, used as the render key and for diagnostics.</summary>
    public required string Id { get; init; }

    /// <summary>Tool, Toggle or Command — which also picks the chrome the control wears.</summary>
    public required ToolbarItemKind Kind { get; init; }

    /// <summary>The tool this control selects. Only meaningful for <see cref="ToolbarItemKind.Tool"/>.</summary>
    public HoldTouchupTool Tool { get; init; } = HoldTouchupTool.None;

    /// <summary>The icon, as an inline SVG fragment.</summary>
    public RenderFragment? Icon { get; init; }

    /// <summary>The tooltip, and the label shown beside the icon in sidebar placement.</summary>
    public required string Label { get; init; }

    /// <summary>The key that picks this control, for the tooltip's "(A)" suffix. Binding stays the host's.</summary>
    public string? ShortcutKey { get; init; }

    /// <summary>Capability gate: false hides the control entirely. Null means always shown.</summary>
    public Func<bool>? Visible { get; init; }

    /// <summary>Capability gate: false renders the control disabled. Null means always enabled.</summary>
    public Func<bool>? Enabled { get; init; }

    /// <summary>Whether a <see cref="ToolbarItemKind.Toggle"/> is currently on.</summary>
    public Func<bool>? IsOn { get; init; }

    /// <summary>Fired by a Toggle or a Command. Tools go through the toolbar's own tool state instead.</summary>
    public EventCallback OnInvoke { get; init; }

    /// <summary>
    /// The contextual row shown while this control is active (a picked Tool, or a Toggle that is on).
    /// </summary>
    public RenderFragment? Row { get; init; }

    /// <summary>Destructive: the control turns red when active, as Delete does.</summary>
    public bool Danger { get; init; }

    /// <summary>Inline style applied while active — the Paint tool tints its button with the paint colour.</summary>
    public Func<string?>? ActiveStyle { get; init; }

    /// <summary>A short badge drawn on the control, e.g. the count of holds marked changed.</summary>
    public Func<string?>? Badge { get; init; }

    /// <summary>Whether this control is currently shown, honouring <see cref="Visible"/>.</summary>
    public bool IsVisible => Visible?.Invoke() ?? true;

    /// <summary>Whether this control is currently interactive, honouring <see cref="Enabled"/>.</summary>
    public bool IsEnabled => Enabled?.Invoke() ?? true;
}
