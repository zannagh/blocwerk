// <copyright file="PanelHoldOverlay.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Holds;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Shared.Ui.Overlay;

/// <summary>The parameters and the paint of <see cref="PanelHoldOverlay"/>.</summary>
public partial class PanelHoldOverlay
{
    /// <summary>The panel's holds, bases before what sits on them.</summary>
    [Parameter]
    public IReadOnlyList<HoldsDocumentHold> Holds { get; set; } = [];

    /// <summary>Holds drawn prominently (a boulder's own holds). Everything else is context.</summary>
    [Parameter]
    public IReadOnlySet<Guid>? HighlightedHoldIds { get; set; }

    /// <summary>Highlighted holds that are footholds — used only for the default colour.</summary>
    [Parameter]
    public IReadOnlySet<Guid>? FootHoldIds { get; set; }

    /// <summary>Fill/stroke for a highlighted hold (the boulder's colours).</summary>
    [Parameter]
    public Func<Guid, (string Fill, string Stroke)>? HoldColor { get; set; }

    /// <summary>Also draw the holds the last update did not find and nobody confirmed (faded, dashed).</summary>
    [Parameter]
    public bool ShowKeptHolds { get; set; }

    /// <summary>Whether a hold that is nobody's highlight is drawn (false while the wall view shows outlines on demand).</summary>
    [Parameter]
    public bool DrawsEveryHold { get; set; } = true;

    /// <summary>Boulders per hold id while the heat map is on, drawn as count badges; null while it is off.</summary>
    [Parameter]
    public IReadOnlyDictionary<Guid, int>? HeatCounts { get; set; }

    private int UsageCount(Guid holdId) =>
        HeatCounts is not null && HeatCounts.TryGetValue(holdId, out var count) ? count : 0;

    private string FillOf(HoldsDocumentHold h, bool highlighted)
    {
        if (highlighted)
        {
            return HoldColor?.Invoke(h.Id).Fill
                   ?? HoldPalette.Rgba(FootHoldIds?.Contains(h.Id) == true ? BoulderHoldColors.Foot : BoulderHoldColors.Normal, 0.55);
        }

        // Context holds: faintly tinted by their own paint colour, dim when a boulder is highlighted.
        return HoldPalette.Fill(h.Color, HighlightedHoldIds is { Count: > 0 } ? 0.12 : 0.35);
    }

    private string StrokeOf(HoldsDocumentHold h, bool highlighted)
    {
        if (highlighted)
        {
            return HoldColor?.Invoke(h.Id).Stroke
                   ?? (FootHoldIds?.Contains(h.Id) == true ? BoulderHoldColors.Foot : BoulderHoldColors.Normal);
        }

        return HighlightedHoldIds is { Count: > 0 } ? "rgba(255,255,255,0.35)" : HoldPalette.StrokeHex(h.Color);
    }
}
