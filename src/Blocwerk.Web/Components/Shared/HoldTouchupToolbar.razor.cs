// <copyright file="HoldTouchupToolbar.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The shared tool row of the hold touch-up surfaces. Both <see cref="TouchupStep"/> and the editable
/// right-hand pane of <see cref="CarryoverReview"/> render it, so the tools exist once instead of as
/// two drifting copies of the old "+ Add hold" / "Remove selected" button pair.
/// It is a pure control surface: every bit of state (the active tool, the current hold size, whether a
/// hold is selected) is owned by the consumer and flows back through the callbacks, so the persistence
/// path stays exactly where it was.
/// </summary>
public partial class HoldTouchupToolbar
{
    /// <summary>
    /// Slider bounds, in thousandths of the normalized panel width: the FULL range
    /// <c>IWallPanelService.UpdateStagedHoldAsync</c> clamps to (0.003–0.200), deliberately not the wall
    /// editor's narrower 3–40. The narrow range cost travel-per-step but broke volumes outright: a staged
    /// hold above r=0.040 could no longer be enlarged, and <see cref="SliderValue"/>'s clamp showed it
    /// pinned at the maximum, so one touch shrank a volume to 0.040. A range that cannot express what the
    /// service stores is what made the slider destructive, so it covers the service's range instead.
    /// Fine adjustment is still exact — the step is one thousandth, and the readout names the value.
    /// </summary>
    private const int SliderMin = 3;
    private const int SliderMax = 200;

    // A per-instance id so the label/input pairing stays unique when two toolbars are ever on screen.
    private readonly string _sliderId = $"hold-size-{Guid.NewGuid():N}";

    /// <summary>The tool currently in effect; the consumer keeps it and reacts to taps accordingly.</summary>
    [Parameter] public HoldTouchupTool Tool { get; set; }

    /// <summary>Raised with the tool the user picked (or <see cref="HoldTouchupTool.None"/> when they clicked the active one again).</summary>
    [Parameter] public EventCallback<HoldTouchupTool> ToolChanged { get; set; }

    /// <summary>The current hold size (normalized radius): what a new hold gets, and what the slider shows.</summary>
    [Parameter] public double Size { get; set; }

    /// <summary>Raised while the slider is dragged, so the consumer's size follows it live.</summary>
    [Parameter] public EventCallback<double> SizeChanged { get; set; }

    /// <summary>
    /// Raised once the slider is released. Separate from <see cref="SizeChanged"/> so a drag persists a
    /// selected hold's new radius exactly once instead of on every intermediate value.
    /// </summary>
    [Parameter] public EventCallback<double> OnSizeCommitted { get; set; }

    /// <summary>True when a hold is selected, which is what makes the slider resize that hold.</summary>
    [Parameter] public bool HasSelection { get; set; }

    private int SliderValue => Math.Clamp((int)Math.Round(Size * 1000), SliderMin, SliderMax);

    // The slider is deliberately explicit about its target: with a hold selected it resizes THAT hold,
    // otherwise it sets the size the next added hold gets.
    private string SizeLabel => HasSelection ? "Size (selected):" : "Size (new holds):";

    private string SizeText => SliderValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string ToolHint => Tool switch
    {
        HoldTouchupTool.Add => "Add: tap the photo to place a hold at the size below.",
        HoldTouchupTool.Delete => "Delete: tap a hold to remove it.",
        HoldTouchupTool.Pipette => "Pipette: tap a hold to copy its size, then keep adding.",
        _ => "Tap a hold to select it, drag it to reposition it, or pick a tool.",
    };

    private async Task PickAsync(HoldTouchupTool tool)
    {
        // Clicking the active tool leaves tool mode, mirroring the old toggle button's cancel.
        var next = Tool == tool ? HoldTouchupTool.None : tool;
        await ToolChanged.InvokeAsync(next);
    }

    private async Task OnSizeInput(ChangeEventArgs e)
    {
        if (ParseRadius(e, out var radius))
        {
            await SizeChanged.InvokeAsync(radius);
        }
    }

    private async Task OnSizeCommit(ChangeEventArgs e)
    {
        if (ParseRadius(e, out var radius))
        {
            await SizeChanged.InvokeAsync(radius);
            await OnSizeCommitted.InvokeAsync(radius);
        }
    }

    private static bool ParseRadius(ChangeEventArgs e, out double radius)
    {
        radius = 0;
        if (!int.TryParse(e.Value?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var val))
        {
            return false;
        }

        radius = Math.Clamp(val, SliderMin, SliderMax) / 1000.0;
        return true;
    }
}
