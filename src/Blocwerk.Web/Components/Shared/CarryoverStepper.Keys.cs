// <copyright file="CarryoverStepper.Keys.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The stepper's own keyboard layer. It has to be its own, not the wizard's: the global dispatcher
/// stands down inside <c>.panel-stepper</c> and the wizard drops its bindings while a sub-view is
/// open, so every key that works here is declared here. Split out of
/// <see cref="CarryoverStepper"/> to keep that partial inside the project's file-size rule.
/// <para>
/// Sub-modes come first (the re-target pick listens for Enter/Escape only), then the touch-up tools
/// (a / m / d / p, see <c>CarryoverStepper.Tools.cs</c>), then this stepper's established review keys —
/// Enter, c, x/Del and the arrows — none of which the tools re-letter.
/// </para>
/// </summary>
public partial class CarryoverStepper
{
    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (Items.Count == 0)
        {
            return;
        }

        if (_interactive)
        {
            await OnRetargetKeyAsync(e.Key);
            return;
        }

        // The touch-up tools get first refusal: a / m / d / p and Escape are free here (this stepper's
        // own keys are Enter, c, x/Del and the arrows), so nothing established is re-lettered.
        if (TryHandleToolKey(e.Key))
        {
            return;
        }

        await OnReviewKeyAsync(e.Key);
    }

    /// <summary>The re-target pick owns the screen while it is open: it listens for Enter and Escape only.</summary>
    private async Task OnRetargetKeyAsync(string key)
    {
        switch (key)
        {
            case "Enter":
                await UseInteractive();
                break;
            case "Escape":
                CancelInteractive();
                break;
        }
    }

    /// <summary>This stepper's established review keys, unchanged by the tools layered in front of them.</summary>
    private async Task OnReviewKeyAsync(string key)
    {
        switch (key)
        {
            case "Enter":
                await Primary();
                break;
            case "c":
            case "C":
                await ToggleChangedOrNothing();
                break;
            case "x":
            case "X":
            case "Delete":
                await RemoveOrDiscard();
                break;
            case "ArrowRight":
                Next();
                break;
            case "ArrowLeft":
                Back();
                break;
        }
    }

    private Task Primary() => Mode == CarryReviewMode.New ? KeepNew() : AcceptCarry();

    // A staged new hold has no "physically changed" verdict to toggle — the key simply does nothing.
    private Task ToggleChangedOrNothing() =>
        Mode == CarryReviewMode.New ? Task.CompletedTask : ToggleChangedKey();

    private Task RemoveOrDiscard() => Mode == CarryReviewMode.New ? DiscardNew() : RemoveOld();
}
