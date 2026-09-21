// <copyright file="PanelOverlapStepper.Pairing.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The stepper's two PAIRING sub-modes, split out of <see cref="PanelOverlapStepper"/> so that partial
/// stays inside the project's file-size rule.
/// <para>
/// The "moved" pick re-points the CURRENT proposal at a different new-panel hold (the matcher proposed
/// the wrong one, or missed it entirely and the user adds it). Manual linking is independent of the
/// proposals altogether: it pairs any neighbour hold with any new-panel hold and accumulates in
/// <c>_manualLinks</c>, so it works even when the matcher proposed nothing at all.
/// </para>
/// <para>
/// Both are mutually exclusive with each other and with the touch-up tools, because all three claim
/// taps on the same two photos — entering one leaves the others, so a tap never means two things.
/// </para>
/// </summary>
public partial class PanelOverlapStepper
{
    private void EnterMoved()
    {
        // The moved pick and the touch-up tools both claim taps on the new-panel photo: entering one
        // leaves the other, so a tap never means two things at once.
        _touchup.Reset();
        _movedMode = true;
        _addMode = false;
        _movedSelectedHoldId = null;
        _warning = null;
        _refocus = true;
    }

    private void CancelMoved()
    {
        _movedMode = false;
        _addMode = false;
        _movedSelectedHoldId = null;
        _refocus = true;
    }

    private async Task OnMovedAddTap((double X, double Y) at)
    {
        var id = await WallPanelService.AddPanelHoldAsync(WallId, PanelId, at.X, at.Y, HoldTouchupSurface.DefaultRadius);
        AddToStagedSet(new PanelHold(id, at.X, at.Y, HoldTouchupSurface.DefaultRadius, null, HoldCategory.Hand));
        _movedSelectedHoldId = id;
        _addMode = false;
    }

    private async Task UseMovedHold()
    {
        var step = _steps[_index];
        if (_movedSelectedHoldId is not { } chosen)
        {
            return;
        }

        if (!TryRecord(new ConfirmedLink(step.HoldAId, chosen, Moved: true)))
        {
            return;
        }

        CancelMoved();
        await Next();
    }

    // ---- Manual linking --------------------------------------------------------
    private void EnterManual()
    {
        if (_neighborPanels.Count == 0)
        {
            return;
        }

        _touchup.Reset();
        _manualMode = true;
        _movedMode = false;
        _addMode = false;
        _manualLeftId = null;
        _manualRightId = null;
        _manualNeighborId ??= _neighborPanels.First().Id;
        _warning = null;
        _manualFocusKey++;
        _refocus = true;
    }

    private void CancelManual()
    {
        _manualMode = false;
        _manualLeftId = null;
        _manualRightId = null;
        _warning = null;
        _refocus = true;
    }

    private void OnManualLeftTap(Guid holdId) => _manualLeftId = holdId;

    private void OnManualRightTap(Guid holdId) => _manualRightId = holdId;

    private void SelectManualNeighbor(Guid neighborId)
    {
        _manualNeighborId = neighborId;

        // The left selection belongs to a specific neighbour; drop it when switching neighbours.
        _manualLeftId = null;
        _manualFocusKey++;
    }

    /// <summary>
    /// Records a free-form neighbour-hold ↔ new-hold pair the matcher never proposed, then clears the
    /// two selections so the user can pair more. Reuses the same "one new hold, one link" guard as the
    /// proposal steps and dedupes against links already marked manually.
    /// </summary>
    private async Task MarkManualOverlap()
    {
        if (_manualLeftId is not { } left || _manualRightId is not { } right)
        {
            return;
        }

        if (IsNewHoldTaken(right, exceptIndex: -1))
        {
            _warning = "That hold is already linked to another neighbour hold — pick a different one.";
            return;
        }

        if (_manualLinks.Any(l => l.NeighborHoldId == left && l.NewHoldId == right))
        {
            _warning = "Those two holds are already marked as overlapping.";
            return;
        }

        _manualLinks.Add(new ConfirmedLink(left, right, Moved: false));
        _manualLeftId = null;
        _manualRightId = null;
        _warning = null;
        await ReportProgressAsync();
    }
}
