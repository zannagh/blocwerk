// <copyright file="HoldTouchupToolbar.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The shared tool row of the hold touch-up surfaces. Both <see cref="TouchupStep"/> and the editable
/// right-hand pane of <see cref="CarryoverReview"/> render it, so the tools exist once instead of as
/// two drifting copies of the old "+ Add hold" / "Remove selected" button pair.
/// It is a pure control surface: every bit of state (the active tool, the current hold size, whether a
/// hold is selected) is owned by the consumer and flows back through the callbacks, so the persistence
/// path stays exactly where it was.
/// </summary>
public partial class HoldTouchupToolbar : IDisposable
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

    /// <summary>
    /// One thousandth of the normalized panel width per press — the slider's own granularity. Coarse
    /// travel is what the slider is for; the steppers exist for the last few thousandths, which is
    /// exactly the adjustment a fingertip cannot make on a 60px-wide range input.
    /// </summary>
    private const int StepSize = 1;

    // Press-and-hold auto-repeat, tuned like a key repeat: a pause long enough that a single tap is
    // unambiguously one step, then a steady stream.
    private const int RepeatDelayMs = 420;
    // 70ms was ~14 round-trips a second: every step awaits SizeChanged through the parent on Blazor
    // Server and re-renders the overlay. A thousandth of the panel width per step does not need that
    // rate to feel immediate.
    private const int RepeatIntervalMs = 140;

    // A per-instance id so the label/input pairing stays unique when two toolbars are ever on screen.
    private readonly string _sliderId = $"hold-size-{Guid.NewGuid():N}";

    // Live value of an in-progress stepper repeat. The Size parameter round-trips through the parent,
    // so it can lag a fast repeat; this is the authoritative running value until the press is released.
    private int _repeatValue;
    private CancellationTokenSource? _repeatCts;

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
        HoldTouchupTool.Move => "Move: drag a hold to reposition it.",
        _ => "Tap a hold to select it, or pick a tool — the move tool is what lets you drag holds.",
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

    /// <summary>
    /// Starts a stepper press: one step immediately, then auto-repeat until the pointer is released.
    /// <paramref name="direction"/> is -1 (smaller) or +1 (bigger).
    /// </summary>
    private async Task BeginRepeatAsync(int direction)
    {
        CancelRepeat();
        _repeatValue = SliderValue;

        // The CTS is armed BEFORE the first step, not after it. ApplyStepAsync awaits
        // SizeChanged.InvokeAsync, which round-trips to the parent on Blazor Server; a pointerup
        // landing on that await used to see a null _repeatCts, return without cancelling, and leave
        // the repeat loop running unattended with nothing holding the button down.
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repeatCts = cts;

        await ApplyStepAsync(direction);

        // And if the release DID land on that await, it has already cancelled and disposed the
        // source; the token is read before the await so this stays legal, and the loop is simply not
        // started. (Token is captured up front because reading cts.Token after Dispose throws.)
        if (!ReferenceEquals(_repeatCts, cts))
        {
            return;
        }

        _ = RepeatAsync(direction, token);
    }

    /// <summary>
    /// The keyboard path. The steppers are pointer-driven so press-and-hold can repeat, which left
    /// Enter/Space on a focused button doing nothing at all. A click synthesized by the keyboard
    /// carries <see cref="MouseEventArgs.Detail"/> 0, so this steps exactly once and commits, while a
    /// pointer press (detail ≥ 1, and already handled by the repeat pair) falls straight through.
    /// </summary>
    private async Task OnStepClickAsync(MouseEventArgs e, int direction)
    {
        if (e.Detail != 0 || _repeatCts is not null)
        {
            return;
        }

        _repeatValue = SliderValue;
        await ApplyStepAsync(direction);
        await OnSizeCommitted.InvokeAsync(_repeatValue / 1000.0);
    }

    private async Task RepeatAsync(int direction, CancellationToken token)
    {
        try
        {
            await Task.Delay(RepeatDelayMs, token);
            while (!token.IsCancellationRequested)
            {
                await InvokeAsync(() => ApplyStepAsync(direction));
                await Task.Delay(RepeatIntervalMs, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Pointer released (or the component went away): nothing to unwind.
        }
    }

    private async Task ApplyStepAsync(int direction)
    {
        var next = Math.Clamp(_repeatValue + (direction * StepSize), SliderMin, SliderMax);
        if (next == _repeatValue)
        {
            return;
        }

        _repeatValue = next;
        await SizeChanged.InvokeAsync(next / 1000.0);
    }

    /// <summary>
    /// Ends a stepper press and commits once, so a held press persists the selected hold's new radius
    /// exactly like releasing the slider does — not on every intermediate repeat tick.
    /// </summary>
    private async Task EndRepeatAsync()
    {
        if (_repeatCts is null)
        {
            return;
        }

        CancelRepeat();
        await OnSizeCommitted.InvokeAsync(_repeatValue / 1000.0);
    }

    private void CancelRepeat()
    {
        if (_repeatCts is null)
        {
            return;
        }

        _repeatCts.Cancel();
        _repeatCts.Dispose();
        _repeatCts = null;
    }

    /// <inheritdoc />
    public void Dispose() => CancelRepeat();

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
