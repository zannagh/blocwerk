// <copyright file="PanelOverlapStepper.Tools.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The hold touch-up tools on the overlap stepper's right-hand (new panel) pane, so a false detection
/// or a missed hold can be fixed while confirming the overlaps instead of after them.
/// <para>
/// Offered ONLY in the big-wall update flow (<see cref="PanelOverlapStepper.NeighbourStaged"/>). The
/// other consumer — the add-a-panel flow in <c>WallPanelGrid</c> — stages its panel at the wall's LIVE
/// generation, and the staged-edit service path is pinned to the in-flight update's staged generation
/// (<c>WallPanelService.StagedEdit.cs</c>), so resize and delete would simply throw there. Adding a
/// live-generation edit path is a service change, deliberately not made here; the add-panel flow keeps
/// exactly the behaviour it had.
/// </para>
/// <para>
/// Tool mode is mutually exclusive with the stepper's own tap modes: the "moved" pick and the manual
/// pairing both claim taps on these photos, so entering either leaves the tool and vice versa. The
/// moved-pick's own "＋ Add hold here" is a different action (it adds the hold the matcher missed and
/// immediately selects it as the moved target) and is left exactly as it was.
/// </para>
/// </summary>
public partial class PanelOverlapStepper
{
    private readonly HoldTouchupSurface _touchup = new();

    /// <summary>The tools exist only where the staged-edit path is valid — see the type remarks.</summary>
    private bool ToolsEnabled => NeighbourStaged;

    // While the moved pick or the manual pairing owns the taps, the tools do not.
    private bool ToolActive => ToolsEnabled && _touchup.Active && !_movedMode && !_manualMode;

    private Guid? RightPaneSelectedId => _movedMode ? _movedSelectedHoldId : _touchup.SelectedHoldId;

    private void SetTouchupTool(HoldTouchupTool tool)
    {
        _touchup.SetTool(tool);
        if (_touchup.Active)
        {
            _movedMode = false;
            _manualMode = false;
            _movedSelectedHoldId = null;
            _warning = null;
        }

        _refocus = true;
    }

    private async Task OnRightHoldTapAsync(Guid holdId)
    {
        if (!ToolActive)
        {
            _movedSelectedHoldId = holdId;
            return;
        }

        switch (_touchup.Tool)
        {
            case HoldTouchupTool.Delete:
                await DeleteTouchupHoldAsync(holdId);
                break;

            case HoldTouchupTool.Pipette:
                _touchup.Sample(holdId, _stagedList);
                break;

            default:
                _touchup.Select(holdId, _stagedList);
                break;
        }
    }

    private async Task OnRightEmptyTapAsync((double X, double Y) at)
    {
        // The moved pick's own add keeps its behaviour (and its selection side effect).
        if (_movedMode)
        {
            await OnMovedAddTap(at);
            return;
        }

        if (!ToolActive)
        {
            return;
        }

        // The same AddPanelHoldAsync the moved pick uses: it already stamps a staged panel's hold at
        // the staged generation, so the two add paths cannot diverge on generation.
        var id = await WallPanelService.AddPanelHoldAsync(WallId, PanelId, at.X, at.Y, _touchup.Radius);
        AddToStagedSet(new PanelHold(id, at.X, at.Y, _touchup.Radius, null));
        _touchup.Added(id);
    }

    private void OnTouchupSizeChanged(double radius) => _touchup.SetRadius(radius);

    private async Task OnTouchupSizeCommittedAsync(double radius)
    {
        if (_touchup.SelectedHoldId is not { } id || !_stagedHolds.TryGetValue(id, out var hold))
        {
            return;
        }

        await MoveStagedHoldAsync(new PanelImageView.HoldGeometry(id, hold.X, hold.Y, radius));
    }

    private async Task MoveStagedHoldAsync(PanelImageView.HoldGeometry g)
    {
        await WallPanelService.UpdateStagedHoldAsync(WallId, g.HoldId, g.X, g.Y, g.Radius);
        if (_stagedHolds.TryGetValue(g.HoldId, out var hold))
        {
            ReplaceInStagedSet(hold with { X = g.X, Y = g.Y, Radius = g.Radius });
        }
    }

    // The steps whose confirmed link was dropped because the staged hold it pointed at was deleted.
    // A step's decision slot cannot say WHY it is null — "discard match" nulls it too — so the ones the
    // bin took away are remembered here instead. Without it, a link confirmed twenty steps ago simply
    // vanished behind a transient warning on the CURRENT step and was never offered again.
    private readonly SortedSet<int> _undecidedByDelete = [];

    private int UndecidedByDeleteCount => _undecidedByDelete.Count;

    private bool CurrentIsUndecidedByDelete => _undecidedByDelete.Contains(_index);

    // Set once the user has been sent back to the first un-decided step on the way out, so the
    // deflection below cannot become a loop they have to fight to finish the panel.
    private bool _undecidedDeflected;

    /// <summary>
    /// On the way to <c>Finish</c>: sends the user back to the first step the bin tool un-decided,
    /// ONCE, rather than letting the panel close over decisions that were taken away from them. Says so
    /// out loud and then stays out of the way — finishing with them still un-decided is allowed, exactly
    /// as finishing with never-decided steps always has been.
    /// </summary>
    private bool TryDeflectToUndecided()
    {
        if (_undecidedByDelete.Count == 0 || _undecidedDeflected)
        {
            return false;
        }

        _undecidedDeflected = true;
        var count = _undecidedByDelete.Count;
        GoToFirstUndecided();
        _warning =
            $"{count} step{(count == 1 ? string.Empty : "s")} lost {(count == 1 ? "its" : "their")} match when you " +
            "deleted a hold — here is the first. Decide it, or finish again to leave them unlinked.";
        return true;
    }

    /// <summary>
    /// Jumps back to the first step whose link the bin tool took away, so it can be decided again.
    /// The entry leaves the set as soon as that step is decided (see <see cref="ClearUndecided"/>).
    /// </summary>
    private void GoToFirstUndecided()
    {
        if (_undecidedByDelete.Count == 0)
        {
            return;
        }

        _index = _undecidedByDelete.Min;
        _warning = null;
        _refocus = true;
    }

    /// <summary>One step has just been decided again, so it is no longer owed to the user.</summary>
    private void ClearUndecided(int index) => _undecidedByDelete.Remove(index);

    /// <summary>
    /// Deletes a staged hold on the new panel and keeps the walk consistent with it. The step list and
    /// the index are untouched (they are proposals about NEIGHBOUR holds, frozen at init), but any
    /// recorded link whose new-hold end was this hold has to go, or the promote would write a HoldLink
    /// to a row that no longer exists. Distinct from the stepper's "Delete hold" action, which marks
    /// the NEIGHBOUR hold as physically removed and is accumulated in <c>_removed</c>.
    /// <para>
    /// Dropping those links un-decides their steps, which may be far behind the current one. They are
    /// recorded in <c>_undecidedByDelete</c> and surfaced as a persistent "N to decide again" affordance
    /// rather than a warning on this step alone, so a decision the user already made is never taken away
    /// silently.
    /// </para>
    /// </summary>
    private async Task DeleteTouchupHoldAsync(Guid id)
    {
        await WallPanelService.DeleteStagedHoldAsync(WallId, id);
        RemoveFromStagedSet(id);
        _touchup.Removed(id);

        if (_movedSelectedHoldId == id)
        {
            _movedSelectedHoldId = null;
        }

        var manual = _manualLinks.RemoveAll(l => l.NewHoldId == id);
        var steps = 0;
        for (var i = 0; i < _decisions.Length; i++)
        {
            if (_decisions[i] is { } d && d.NewHoldId == id)
            {
                _decisions[i] = null;

                // A removed neighbour hold is already settled — re-offering it would ask the user to
                // confirm a hold they said is gone.
                if (!_removed.Contains(d.NeighborHoldId))
                {
                    _undecidedByDelete.Add(i);
                }

                steps++;
            }
        }

        if (steps > 0)
        {
            // A fresh delete is a fresh set of taken-away decisions: the one-time deflection on the way
            // out arms again, so the second delete is shown as loudly as the first.
            _undecidedDeflected = false;
        }

        if (manual + steps > 0)
        {
            _warning = DroppedWarning(manual, steps);
            await ReportProgressAsync();
        }
    }

    /// <summary>Says exactly what the delete took away, because both halves are the user's own work.</summary>
    private static string DroppedWarning(int manual, int steps)
    {
        var parts = new List<string>();
        if (steps > 0)
        {
            parts.Add($"{steps} confirmed match{(steps == 1 ? string.Empty : "es")}");
        }

        if (manual > 0)
        {
            parts.Add($"{manual} manual link{(manual == 1 ? string.Empty : "s")}");
        }

        var what = string.Join(" and ", parts);
        return steps > 0
            ? $"That hold was linked — {what} went with it. Use \"Decide again\" below to revisit those steps."
            : $"That hold was linked — {what} went with it.";
    }

    private void AddToStagedSet(PanelHold hold)
    {
        _stagedHolds[hold.Id] = hold;
        _stagedList = [.. _stagedList, hold];
    }

    private void ReplaceInStagedSet(PanelHold hold)
    {
        _stagedHolds[hold.Id] = hold;
        _stagedList = _stagedList.Select(h => h.Id == hold.Id ? hold : h).ToList();
    }

    private void RemoveFromStagedSet(Guid id)
    {
        _stagedHolds.Remove(id);
        _stagedList = _stagedList.Where(h => h.Id != id).ToList();
    }
}
