// <copyright file="CrossGenLinkTool.Tools.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The hold touch-up tools on the side-by-side confirm's RIGHT pane (the staged new-gen holds), so a
/// false detection spotted while matching can be deleted — or a missed hold added — without backing
/// out to the overview. The left pane is the previous, already-live generation and stays read-only:
/// the staged-edit service path is structurally unable to touch a live hold at all.
/// <para>
/// Like the stepper, this component injects no service: <see cref="CarryoverReview"/> passes its
/// staged add / resize / delete path down as callbacks, so the <c>needsReview</c> default lives in
/// one place. Add is a <see cref="Func{T, TResult}"/> because it hands the new hold's id back.
/// </para>
/// <para>
/// Tool mode and the LINK pick are mutually exclusive: both claim taps on the same photos, so picking
/// a tool drops the pending link selection and, while a tool is active, taps on the left pane are
/// ignored rather than half-arming a link the user is not making.
/// </para>
/// </summary>
public partial class CrossGenLinkTool
{
    /// <summary>Adds a staged hold at a normalized point and size, returning its id.</summary>
    [Parameter] public Func<(double X, double Y, double Radius), Task<Guid?>>? AddStagedHold { get; set; }

    /// <summary>Moves or resizes a staged hold on the new-gen pane.</summary>
    [Parameter] public EventCallback<PanelImageView.HoldGeometry> OnHoldGeometryChanged { get; set; }

    /// <summary>Deletes a staged hold from the new-gen pane.</summary>
    [Parameter] public EventCallback<Guid> OnDeleteHold { get; set; }

    private readonly HoldTouchupSurface _touchup = new();

    private ElementReference _rootRef;
    private bool _refocus = true;

    /// <summary>The tools only exist when the host wired the editing path up.</summary>
    private bool ToolsEnabled => AddStagedHold is not null;

    private bool RightPaneEditable => ToolsEnabled && _touchup.Active;

    private Guid? RightPaneSelectedId => _touchup.Active ? _touchup.SelectedHoldId : _rightHoldId;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The tool keys are this component's own (the global dispatcher stands down inside
        // .crossgen-tool), so the root has to hold focus for them to fire.
        if (!_refocus)
        {
            return;
        }

        _refocus = false;
        try
        {
            await _rootRef.FocusAsync(preventScroll: true);
        }
        catch (Exception)
        {
            // Focus is a keyboard-shortcut nicety; never fatal.
        }
    }

    // Picking a tool drops any pending link selection — one tap, one meaning.
    private void SetTouchupTool(HoldTouchupTool tool)
    {
        _touchup.SetTool(tool);
        if (_touchup.Active)
        {
            ClearLinkSelection();
        }

        _refocus = true;
    }

    private async Task OnRightHoldTapAsync(Guid holdId)
    {
        if (!_touchup.Active)
        {
            _rightHoldId = holdId;
            return;
        }

        switch (_touchup.Tool)
        {
            case HoldTouchupTool.Delete:
                await OnDeleteHold.InvokeAsync(holdId);
                _touchup.Removed(holdId);
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

    private async Task OnTouchupSizeCommittedAsync(double radius)
    {
        if (_touchup.SelectedHoldId is not { } id
            || NewHolds.FirstOrDefault(h => h.Id == id) is not { } hold)
        {
            return;
        }

        await OnHoldGeometryChanged.InvokeAsync(new PanelImageView.HoldGeometry(id, hold.X, hold.Y, radius));
    }

    /// <summary>
    /// This component's only keyboard bindings. It had none at all before — the global dispatcher
    /// stands down inside <c>.crossgen-tool</c> and the wizard drops its own keys while a sub-view is
    /// open — so every letter was free and the tools take the same a / m / d / p the wizard and the
    /// touch-up steps use. Escape leaves the tool and, deliberately, nothing else: it never discards.
    /// The linking actions stay on their buttons, so no existing muscle memory is claimed here.
    /// </summary>
    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (!ToolsEnabled)
        {
            return;
        }

        if (HoldTouchupSurface.ToolForKey(e.Key) is { } tool)
        {
            SetTouchupTool(_touchup.Tool == tool ? HoldTouchupTool.None : tool);
            return;
        }

        if (e.Key == "Escape" && _touchup.Active)
        {
            _touchup.Reset();
        }
    }
}
