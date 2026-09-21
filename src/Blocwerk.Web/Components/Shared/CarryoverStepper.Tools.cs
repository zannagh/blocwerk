// <copyright file="CarryoverStepper.Tools.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The hold touch-up tools on the stepper's right-hand (new centre) pane, so a wrong hold spotted
/// mid-review can be fixed where it is seen instead of by backing out to the overview.
/// <para>
/// The stepper injects NO service: <see cref="CarryoverReview"/> already owns the staged add / resize /
/// delete path (and with it the deliberate <c>needsReview: true</c> default), so the three operations
/// are passed down as callbacks. Add is a <see cref="Func{T, TResult}"/> rather than an
/// <see cref="EventCallback"/> because it has to hand the new hold's id back so the fresh hold can be
/// selected; the parent renders itself explicitly for that one path.
/// </para>
/// <para>
/// Tool mode and the re-target pick (<c>_interactive</c>) are mutually exclusive: both claim taps on
/// the same photo, so entering one leaves the other.
/// </para>
/// </summary>
public partial class CarryoverStepper
{
    /// <summary>
    /// Adds a staged hold at a normalized point and size, returning its id (null when the host does
    /// not offer editing). Unset leaves the stepper exactly as it was before the tools existed.
    /// </summary>
    [Parameter] public Func<(double X, double Y, double Radius), Task<Guid?>>? AddStagedHold { get; set; }

    /// <summary>Moves or resizes a staged hold on the new centre panel.</summary>
    [Parameter] public EventCallback<PanelImageView.HoldGeometry> OnHoldGeometryChanged { get; set; }

    /// <summary>Deletes a staged hold from the new centre panel.</summary>
    [Parameter] public EventCallback<Guid> OnDeleteHold { get; set; }

    private readonly HoldTouchupSurface _touchup = new();

    /// <summary>The tools only exist when the host wired the editing path up.</summary>
    private bool ToolsEnabled => AddStagedHold is not null;

    // Taps on the right pane are claimed by the re-target pick OR by a tool, never both.
    private bool RightPaneInteractive => _interactive || _touchup.Active;

    private bool RightPaneEditable => ToolsEnabled && !_interactive && _touchup.Active;

    private Guid? RightPaneSelectedId => _interactive ? _selectedNewId : _touchup.SelectedHoldId;

    /// <summary>
    /// New-hold mode only: the staged hold this item is about has been deleted (here with the bin tool,
    /// or on the overview). The item deliberately STAYS in the frozen walk so the index cannot shift
    /// under the user mid-review — there is simply nothing left to keep or discard.
    /// </summary>
    private bool CurrentNewHoldGone =>
        Mode == CarryReviewMode.New && Current?.NewHoldId is { } id && !_newById.ContainsKey(id);

    // Picking a tool leaves the re-target pick, and refocuses the root so the stepper's own keys
    // keep working after the toolbar button took focus.
    private void SetTouchupTool(HoldTouchupTool tool)
    {
        _touchup.SetTool(tool);
        if (_touchup.Active && _interactive)
        {
            _interactive = false;
            _selectedNewId = null;
        }

        _refocus = true;
    }

    private async Task OnRightHoldTapAsync(Guid holdId)
    {
        if (_interactive)
        {
            _selectedNewId = holdId;
            return;
        }

        switch (_touchup.Tool)
        {
            case HoldTouchupTool.Delete:
                await DeleteTouchupHoldAsync(holdId);
                break;

            case HoldTouchupTool.Pipette:
                _touchup.Sample(holdId, NewHolds);
                break;

            default:
                _touchup.Select(holdId, NewHolds);
                break;
        }
    }

    private async Task OnTouchupEmptyTapAsync((double X, double Y) at)
    {
        if (AddStagedHold is null)
        {
            return;
        }

        if (await AddStagedHold((at.X, at.Y, _touchup.Radius)) is { } id)
        {
            _touchup.Added(id);
        }
    }

    private void OnTouchupSizeChanged(double radius) => _touchup.SetRadius(radius);

    // Slider released: with a hold selected that hold is resized, through the same geometry callback a
    // drag uses. With nothing selected the slider only set the size for the next add.
    private async Task OnTouchupSizeCommittedAsync(double radius)
    {
        if (_touchup.SelectedHoldId is not { } id
            || NewHolds.FirstOrDefault(h => h.Id == id) is not { } hold)
        {
            return;
        }

        await OnHoldGeometryChanged.InvokeAsync(new PanelImageView.HoldGeometry(id, hold.X, hold.Y, radius));
    }

    private async Task DeleteTouchupHoldAsync(Guid id)
    {
        await OnDeleteHold.InvokeAsync(id);
        _touchup.Removed(id);
    }

    /// <summary>
    /// The tool keys (a / m / d / p, plus Escape to leave the tool). They are bound HERE rather than by
    /// the wizard because the global dispatcher stands down inside <c>.panel-stepper</c> and the wizard
    /// drops its own bindings while a sub-view is open. None of them collides with this stepper's
    /// established review keys (Enter, c, x/Del, arrows). Returns true when the key was a tool key.
    /// </summary>
    private bool TryHandleToolKey(string key)
    {
        if (!ToolsEnabled || _interactive)
        {
            return false;
        }

        if (HoldTouchupSurface.ToolForKey(key) is { } tool)
        {
            _touchup.Toggle(tool);
            return true;
        }

        // Escape never discards anything here — it only drops out of the tool.
        if (key == "Escape" && _touchup.Active)
        {
            _touchup.Reset();
            return true;
        }

        return false;
    }
}
