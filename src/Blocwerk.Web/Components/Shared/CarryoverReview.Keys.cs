// <copyright file="CarryoverReview.Keys.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The keyboard seam between this phase and the wizard that hosts it: the entry points the wizard's
/// Enter/a/x bindings call, plus the sub-view notification that tells the wizard when to drop those
/// bindings because another surface owns the screen. Separate from the review state in
/// <see cref="CarryoverReview"/> because it is purely the shortcut contract — every method here only
/// guards and then calls what the matching button calls — and it keeps that partial under the
/// project's file-size rule.
/// </summary>
public partial class CarryoverReview
{
    /// <summary>True while a sub-view (focused stepper or cross-gen link tool) owns the screen.</summary>
    private bool SubViewOpen => _reviewMode is not null || _crossGenOpen;

    // Last sub-view state the wizard was told about. The sub-views are opened and closed from half a
    // dozen places across the partials, so the change is noticed once at render time instead of
    // threading a callback through every one of them.
    private bool _subViewNotified;

    /// <summary>
    /// Keyboard entry point for the wizard's Enter binding on this phase. It is inert while a sub-view
    /// is open — that surface has its own Enter, and continuing out from under it would skip decisions
    /// the user is still making.
    /// </summary>
    public async Task TryContinueAsync()
    {
        if (_loading || SubViewOpen)
        {
            return;
        }

        await Continue();
    }

    /// <summary>Keyboard entry point for the wizard's "a" binding (toggle add-hold mode).</summary>
    public void TryToggleAddMode()
    {
        if (_loading || SubViewOpen)
        {
            return;
        }

        ToggleAddMode();
        StateHasChanged();
    }

    /// <summary>
    /// Keyboard entry point for the wizard's "x" binding. Mirrors the Remove button's disabled state:
    /// with no hold selected the key does nothing.
    /// </summary>
    public async Task TryRemoveSelectedNewHoldAsync()
    {
        if (_loading || SubViewOpen || _selectedNewHoldId is null)
        {
            return;
        }

        await RemoveSelectedNewHold();
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (SubViewOpen != _subViewNotified)
        {
            _subViewNotified = SubViewOpen;
            await OnSubViewOpenChanged.InvokeAsync(_subViewNotified);
        }
    }
}
