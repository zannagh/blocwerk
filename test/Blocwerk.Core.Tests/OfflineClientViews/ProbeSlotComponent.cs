// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Views;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>What PR 3.13 mounts into a slot, in miniature: it names what it was shown and keeps the default content.</summary>
public sealed class ProbeSlotComponent : ComponentBase
{
    [Parameter]
    public ViewSlotContext Context { get; set; } = null!;

    [Parameter]
    public RenderFragment? DefaultContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "probe");
        builder.AddAttribute(2, "data-wall", Context.Wall.WallId.ToString());
        builder.AddAttribute(3, "data-boulder", Context.Boulder?.Name ?? "none");
        builder.AddAttribute(4, "data-token", Context.ShareToken ?? "none");
        builder.AddContent(5, DefaultContent);
        builder.CloseElement();
    }
}
