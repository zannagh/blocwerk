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
    // Toolbar state — the active tool, the size newly added holds get, and the selection — shared with
    // every other touch-up surface so the select / sample / reset rules cannot drift between them.
    private readonly HoldTouchupSurface _touchup = new();

    /// <summary>
    /// A tap on a hold in the new pane, routed by the active tool: the bin deletes it, the pipette
    /// adopts its size for the next adds, and otherwise it is simply selected.
    /// </summary>
    private async Task OnNewHoldTapAsync(Guid id)
    {
        switch (_touchup.Tool)
        {
            case HoldTouchupTool.Delete:
                await RemoveNewHoldAsync(id);
                break;

            case HoldTouchupTool.Pipette:
                _touchup.Sample(id, _newHolds);
                break;

            default:
                _touchup.Select(id, _newHolds);
                break;
        }
    }

    private void SetTool(HoldTouchupTool tool) => _touchup.SetTool(tool);

    // Live slider feedback: the size a new hold gets always follows the slider.
    private void OnSizeChanged(double radius) => _touchup.SetRadius(radius);

    // Slider released: with a hold selected that hold is resized, through the same geometry/persistence
    // path a drag uses. With nothing selected the slider only set the size for the next add.
    private async Task OnSizeCommittedAsync(double radius)
    {
        if (_touchup.SelectedHoldId is not { } id || _newHolds.FirstOrDefault(h => h.Id == id) is not { } hold)
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
        // The Add tool stays active for a run of adds; the fresh hold is selected so the slider can
        // fine-tune it straight away.
        if (await AddNewHoldAsync((at.X, at.Y, _touchup.Radius)) is { } id)
        {
            _touchup.Added(id);
        }
    }

    /// <summary>
    /// The one staged-add path, shared by this pane's toolbar and by the review sub-views (the focused
    /// stepper and the cross-gen link tool), which reach it as a parameter instead of injecting
    /// <c>IWallPanelService</c> a second time. Returns the new hold's id so the caller can select it.
    /// <para>
    /// needsReview is left at the service default (true) — unlike the touch-up step, a hold added
    /// during the carryover is a real change on the new wall, not a detection correction. Routing the
    /// sub-views through here is what keeps that asymmetry a single decision.
    /// </para>
    /// <para>
    /// It is a <see cref="Func{T, TResult}"/> rather than an <c>EventCallback</c> because the callers
    /// need the id back; that forgoes the automatic re-render an EventCallback would trigger, so this
    /// component renders itself after the reload — the sub-views draw the staged holds from the
    /// <c>NewHolds</c> parameter, which only moves when this component renders.
    /// </para>
    /// </summary>
    private async Task<Guid?> AddNewHoldAsync((double X, double Y, double Radius) at)
    {
        var id = await WallPanelService.AddStagedHoldAsync(WallId, CenterPanelId, at.X, at.Y, at.Radius);
        await ReloadNewHoldsAsync();
        StateHasChanged();
        return id;
    }

    private async Task RemoveSelectedNewHold()
    {
        if (_touchup.SelectedHoldId is not { } id)
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
            // Deliberately UNCONFIRMED. The user decided "that detection is not a hold"; they decided
            // nothing about the old holds that were pointing at it, which have just lost their twin and
            // are now carried BLIND at their old position — the state that most needs a human. Writing
            // it confirmed would attribute the sign-off to whoever pressed the bin and drop every one of
            // those holds out of the attention queue and the review lists unseen. False also lets the
            // policy's verdict-changed rule clear an EARLIER sign-off, which was about the match that
            // just went away. Every one of these holds is rewritten, including the co-updated neighbour
            // panels' — a decision must never point at a staged hold that no longer exists — and the
            // reviewed list reads the whole map, so an out-of-panel sign-off can still be un-reviewed.
            var fallback = d with { NewHoldId = null, Confirmed = false };
            _decisions[oldId] = fallback;
            orphaned.Add(fallback);
        }

        await SaveCarryAllAsync(orphaned);

        _touchup.Removed(id);

        await ReloadNewHoldsAsync();
    }
}
