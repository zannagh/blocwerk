// <copyright file="CarryoverStepper.Actions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What the stepper's buttons and keys actually DO to one hold: the carry verdict it emits, the
/// "physically changed" toggle, the removal, the keep/discard for a staged new hold, and the manual
/// re-target pick. Split out of <see cref="CarryoverStepper"/> so that partial stays inside the
/// project's file-size rule; the state they read (<c>_changed</c>, <c>_retargeted</c>, <c>Current</c>)
/// lives there.
/// <para>
/// Every carry verdict leaves here through <c>EmitCarry</c>, so there is exactly one place that decides
/// what the parent is told — and the parent writes all of them as CONFIRMED, because anything that
/// arrives from this surface is a person deciding.
/// </para>
/// </summary>
public partial class CarryoverStepper
{
    // Records the current per-hold decision: Carried by default, Changed when the toggle is on,
    // always with the effective (possibly re-targeted) new-hold association.
    private async Task EmitCarry()
    {
        if (Current?.OldHoldId is { } old)
        {
            await OnCarryDecision.InvokeAsync(new CarryDecisionChange(old, CurrentKind, EffectiveNewHoldId));
        }
    }

    private async Task AcceptCarry()
    {
        await EmitCarry();
        Next();
    }

    private async Task ToggleChanged(ChangeEventArgs e)
    {
        if (Current?.OldHoldId is not { } old)
        {
            return;
        }

        if (e.Value is true)
        {
            _changed.Add(old);
        }
        else
        {
            _changed.Remove(old);
        }

        await EmitCarry();
    }

    private async Task ToggleChangedKey()
    {
        if (Current?.OldHoldId is not { } old)
        {
            return;
        }

        if (!_changed.Add(old))
        {
            _changed.Remove(old);
        }

        await EmitCarry();
    }

    private async Task RemoveOld()
    {
        if (Current?.OldHoldId is { } old)
        {
            await OnCarryDecision.InvokeAsync(new CarryDecisionChange(old, CarryKind.Removed, null));
        }

        Next();
    }

    private async Task KeepNew()
    {
        // The staged hold was deleted (bin tool) while the walk was open: its slot stays in the frozen
        // order so the index cannot shift, but there is nothing left to keep.
        if (CurrentNewHoldGone)
        {
            Next();
            return;
        }

        if (Current?.NewHoldId is { } id)
        {
            await OnNewDecision.InvokeAsync(new NewDecisionChange(id, Discarded: false));
        }

        Next();
    }

    private async Task DiscardNew()
    {
        if (CurrentNewHoldGone)
        {
            Next();
            return;
        }

        if (Current?.NewHoldId is { } id)
        {
            await OnNewDecision.InvokeAsync(new NewDecisionChange(id, Discarded: true));
        }

        Next();
    }

    private void EnterInteractive()
    {
        // The re-target pick and the touch-up tools both claim taps on the right photo: entering one
        // leaves the other, so a tap never means two things at once.
        _touchup.Reset();
        _interactive = true;
        _selectedNewId = EffectiveNewHoldId;
        _refocus = true;
    }

    private void CancelInteractive()
    {
        _interactive = false;
        _selectedNewId = null;
        _refocus = true;
    }

    // Manual re-target only records "this old hold is this new hold". It is independent of the
    // "changed" toggle, and it does NOT advance — the user may still toggle changed or remove.
    private async Task UseInteractive()
    {
        if (Current?.OldHoldId is { } old && _selectedNewId is { } chosen)
        {
            _retargeted[old] = chosen;
            _interactive = false;
            _selectedNewId = null;
            _refocus = true;
            await EmitCarry();
        }
    }
}
