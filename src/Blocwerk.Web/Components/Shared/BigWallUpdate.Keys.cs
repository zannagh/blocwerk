// <copyright file="BigWallUpdate.Keys.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wizard's keyboard layer: the per-phase key declarations, the render-time reconcile that keeps
/// the browser scope in sync with the current phase, the shortcut dispatcher and the Enter (primary)
/// action. It lives apart from the phase orchestration in <see cref="BigWallUpdate"/> because it is a
/// self-contained concern — it only reads the phase and calls the same methods the buttons call — and
/// keeping it here holds the main partial under the project's file-size rule.
/// </summary>
public partial class BigWallUpdate
{
    // ---- Keyboard shortcuts -----------------------------------------------------
    // The wizard is a keyboard-heavy flow (Enter to step forward, s to skip a touch-up) but its
    // available actions change completely from phase to phase, so the key set is re-declared per
    // phase rather than declared once: a key whose button is not on screen must not exist at all.
    private KeyboardShortcutScope? _keys;
    private string? _keysDeclared;
    private bool _carryoverSubViewOpen;

    private BigWallUpdateUploader? _uploader;
    private TouchupStep? _detectedStep;
    private TouchupStep? _touchupStep;
    private CarryoverReview? _carryover;

    private TouchupStep? CurrentTouchupStep => _phase == WallUpdatePhase.Detected ? _detectedStep : _touchupStep;

    /// <summary>
    /// The keys that are live for the current phase. Neighbours declares nothing on purpose: the
    /// overlap stepper owns its own keys, so claiming Enter here would fight it. The dispatcher also
    /// stands down entirely while a stepper is on screen, which makes this belt and braces rather
    /// than the only guard — keep it anyway, so the phase's key set stays honest on its own terms.
    /// Carryover goes quiet the same way while one of its sub-views is open.
    /// </summary>
    private string[] KeysForPhase() => _superseded ? [] : _phase switch
    {
        // "Not now" simply closes the flow and leaves the staged update untouched, so Escape is safe.
        WallUpdatePhase.ResumePrompt => ["Enter", "Escape"],

        // Nothing is staged yet, so Escape is the plain Cancel button — it discards nothing.
        WallUpdatePhase.Upload => ["Enter", "Escape"],

        WallUpdatePhase.Detected => ["Enter", "s", "a", "x"],
        WallUpdatePhase.Carryover => _carryoverSubViewOpen ? [] : ["Enter", "a", "x"],
        WallUpdatePhase.Touchup => ["Enter", "s", "a", "x"],
        WallUpdatePhase.Confirm => ["Enter"],

        // The update is already applied; Enter/Escape both just close the finished flow.
        WallUpdatePhase.Done => ["Enter", "Escape"],

        // Loading, Working and Neighbours: no wizard-level keys.
        _ => [],
    };

    private Task OnCarryoverSubViewOpenChanged(bool open)
    {
        _carryoverSubViewOpen = open;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task OnShortcut(string key)
    {
        switch (key)
        {
            case "Enter":
                await PrimaryActionAsync();
                break;

            case "s":
                await SkipShortcutAsync();
                break;

            case "a":
                AddModeShortcut();
                break;

            case "x":
                await RemoveHoldShortcutAsync();
                break;

            case "Escape":
                await EscapeShortcutAsync();
                break;
        }

        // The callback arrives outside a Blazor UI event, so render explicitly.
        StateHasChanged();
    }

    // Skip exists on the touch-up phases only; elsewhere the key is not declared at all.
    private async Task SkipShortcutAsync()
    {
        if (_phase == WallUpdatePhase.Detected)
        {
            await OnDetectedContinue();
        }
        else if (_phase == WallUpdatePhase.Touchup)
        {
            await OnTouchupSkip();
        }
    }

    private void AddModeShortcut()
    {
        if (_phase is WallUpdatePhase.Detected or WallUpdatePhase.Touchup)
        {
            CurrentTouchupStep?.TryToggleAddMode();
        }
        else if (_phase == WallUpdatePhase.Carryover)
        {
            _carryover?.TryToggleAddMode();
        }
    }

    private async Task RemoveHoldShortcutAsync()
    {
        if (_phase is (WallUpdatePhase.Detected or WallUpdatePhase.Touchup) && CurrentTouchupStep is { } step)
        {
            await step.TryRemoveSelectedHoldAsync();
        }
        else if (_phase == WallUpdatePhase.Carryover && _carryover is { } carryover)
        {
            await carryover.TryRemoveSelectedNewHoldAsync();
        }
    }

    // Deliberately NOT wired to Discard: discarding is irreversible and unconfirmed, so Escape only
    // closes the flow where closing throws nothing away.
    private async Task EscapeShortcutAsync()
    {
        // The conflict prompt's Escape is its own Cancel: it backs out to the upload form without
        // touching the other admin's update, which closing the whole flow would also do — but this
        // keeps the photos the user already picked.
        if (_conflict is not null)
        {
            CancelConflict();
            return;
        }

        if (_phase is WallUpdatePhase.ResumePrompt or WallUpdatePhase.Upload or WallUpdatePhase.Done)
        {
            await Close();
        }
    }

    private async Task PrimaryActionAsync()
    {
        switch (_phase)
        {
            case WallUpdatePhase.ResumePrompt:
                await ResumeExistingAsync();
                break;

            case WallUpdatePhase.Upload:
                // While the conflict prompt is up, Enter is its primary action — continuing the
                // update that is already open. Never the destructive takeover.
                if (_conflict is not null)
                {
                    await ResumeConflictingAsync();
                    break;
                }

                // TryStartAsync re-checks the button's own disabled condition, so a premature Enter
                // (no centre photo, a photo still being prepared) does nothing.
                if (_uploader is { } uploader)
                {
                    await uploader.TryStartAsync();
                }

                break;

            case WallUpdatePhase.Detected:
                await OnDetectedContinue();
                break;

            case WallUpdatePhase.Carryover:
                if (_carryover is { } carryover)
                {
                    await carryover.TryContinueAsync();
                }

                break;

            case WallUpdatePhase.Touchup:
                await OnTouchupContinue();
                break;

            case WallUpdatePhase.Confirm:
                await Apply();
                break;

            case WallUpdatePhase.Done:
                await Close();
                break;
        }
    }

    // Phases are switched from a dozen places (and from async continuations), so the key set is
    // reconciled after every render instead of at each transition — one place, no drift.
    private async Task ReconcileShortcutsAsync(bool firstRender)
    {
        if (firstRender)
        {
            _keys = new KeyboardShortcutScope(JS, OnShortcut, KeyGate);
        }

        var keys = KeysForPhase();
        var declared = string.Join(',', keys);
        if (_keys is not null && declared != _keysDeclared)
        {
            _keysDeclared = declared;
            await _keys.SetKeysAsync(keys);
        }
    }

    private void DisposeShortcuts()
    {
        if (_keys is not null)
        {
            // Fire-and-forget like the edit-guard clear: unregistering is a one-way JS call and a
            // torn-down circuit no-ops. The dispatcher also drops a handler whose ref is dead.
            _ = _keys.DisposeAsync();
        }
    }
}
