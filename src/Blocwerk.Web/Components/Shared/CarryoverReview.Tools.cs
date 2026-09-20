// <copyright file="CarryoverReview.Tools.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The editable right-hand pane of the carryover review: the shared <see cref="HoldTouchupToolbar"/>'s
/// state (active tool, current hold size, selection) and the staged add / move / resize / delete edits
/// it drives. Split out of <see cref="CarryoverReview"/> because it is the editing surface rather than
/// the carry decisions — and it keeps that partial under the project's file-size rule. Note the one
/// deliberate asymmetry with <see cref="TouchupStep"/>: holds added here keep the service's default
/// <c>needsReview</c> (true), because a hold added during the carryover IS a change on the new wall.
/// </summary>
public partial class CarryoverReview
{
    // Normalized default radius for a user-added hold on the new image (~2% of the panel).
    private const double DefaultNewHoldRadius = 0.02;

    // Toolbar state: the active tool and the size newly added holds get (pipette- or slider-set).
    private HoldTouchupTool _tool;
    private double _newHoldRadius = DefaultNewHoldRadius;
    private Guid? _selectedNewHoldId;

    /// <summary>
    /// A tap on a hold in the new pane, routed by the active tool: the bin deletes it, the pipette
    /// adopts its size for the next adds, and otherwise it is simply selected.
    /// </summary>
    private async Task OnNewHoldTapAsync(Guid id)
    {
        switch (_tool)
        {
            case HoldTouchupTool.Delete:
                await RemoveNewHoldAsync(id);
                break;

            case HoldTouchupTool.Pipette:
                SampleSize(id);
                break;

            default:
                SelectNewHold(id);
                break;
        }
    }

    /// <summary>
    /// Selects a hold AND adopts its radius as the toolbar's size, exactly as the wall editor does on
    /// select and on drag-start. Without this the slider still showed the last add-size while its label
    /// read "Size (selected):", so one nudge resized the selected hold to a value the user never chose —
    /// a 40% shrink on a hold larger than the default.
    /// </summary>
    private void SelectNewHold(Guid id)
    {
        _selectedNewHoldId = id;
        if (_newHolds.FirstOrDefault(h => h.Id == id) is { } hold)
        {
            _newHoldRadius = hold.Radius;
        }
    }

    // Pipette: adopt the tapped hold's radius and drop into Add mode, which is what the sample is for.
    // The selection is cleared so the slider describes the NEXT hold, not the one just sampled.
    private void SampleSize(Guid id)
    {
        if (_newHolds.FirstOrDefault(h => h.Id == id) is { } hold)
        {
            _newHoldRadius = hold.Radius;
        }

        _selectedNewHoldId = null;
        _tool = HoldTouchupTool.Add;
    }

    // Mutually exclusive tools, mirroring the wall editor's SetMode.
    private void SetTool(HoldTouchupTool tool)
    {
        _tool = tool;
        if (tool is HoldTouchupTool.Add or HoldTouchupTool.Pipette)
        {
            _selectedNewHoldId = null;
        }
    }

    // Live slider feedback: the size a new hold gets always follows the slider.
    private void OnSizeChanged(double radius) => _newHoldRadius = radius;

    // Slider released: with a hold selected that hold is resized, through the same geometry/persistence
    // path a drag uses. With nothing selected the slider only set the size for the next add.
    private async Task OnSizeCommittedAsync(double radius)
    {
        if (_selectedNewHoldId is not { } id || _newHolds.FirstOrDefault(h => h.Id == id) is not { } hold)
        {
            return;
        }

        await OnNewHoldGeometryChanged(new PanelImageView.HoldGeometry(id, hold.X, hold.Y, radius));
    }

    private async Task OnNewHoldGeometryChanged(PanelImageView.HoldGeometry g)
    {
        await WallPanelService.UpdateStagedHoldAsync(WallId, g.HoldId, g.X, g.Y, g.Radius);
        await ReloadNewHoldsAsync();
    }

    private async Task OnNewEmptyTap((double X, double Y) at)
    {
        // needsReview is left at the service default (true) here — unlike the touch-up step, an added
        // hold during the carryover is a real change on the new wall, not a detection correction.
        var id = await WallPanelService.AddStagedHoldAsync(WallId, CenterPanelId, at.X, at.Y, _newHoldRadius);

        // The Add tool stays active for a run of adds; the fresh hold is selected so the slider can
        // fine-tune it straight away.
        _selectedNewHoldId = id;
        await ReloadNewHoldsAsync();
    }

    private async Task RemoveSelectedNewHold()
    {
        if (_selectedNewHoldId is not { } id)
        {
            return;
        }

        await RemoveNewHoldAsync(id);
    }

    private async Task RemoveNewHoldAsync(Guid id)
    {
        await WallPanelService.DeleteStagedHoldAsync(WallId, id);

        // Any carry decision that pointed at this now-deleted staged twin falls back to a carry in
        // place (no twin), so no decision references a hold that no longer exists. The fallbacks are
        // written through too, or a resume would restore a twin that has been deleted.
        var orphaned = new List<CarryoverDecision>();
        foreach (var (oldId, d) in _decisions.Where(kv => kv.Value.NewHoldId == id).ToList())
        {
            var fallback = d with { NewHoldId = null };
            _decisions[oldId] = fallback;
            orphaned.Add(fallback);
        }

        await SaveCarryAllAsync(orphaned);

        if (_selectedNewHoldId == id)
        {
            _selectedNewHoldId = null;
        }

        await ReloadNewHoldsAsync();
    }
}
