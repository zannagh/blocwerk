// <copyright file="WallDetail.Redetect.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Redetect;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Re-detect on the panel being edited: its own photo and its own holds. The detector runs first and changes nothing;
/// what it would do is shown in one confirm (<see cref="RedetectOutcomes"/>), and only the confirm applies it. Never
/// with unsaved hold edits pending, which would be judged against holds that are not stored yet.
/// </summary>
public partial class WallDetail
{
    /// <summary>
    /// Off until the detector stops re-adding things a person already turned down: on a real wall one press added
    /// hundreds of holds on markers, mats and spots answered "Not a hold" (see issue #127).
    /// </summary>
    private const bool RedetectAvailable = false;

    private PanelRedetectPlan? redetectPlan;
    private bool redetectBusy;

    [Inject]
    private IPanelRedetectService PanelRedetect { get; set; } = null!;

    private async Task OnRedetectPanel()
    {
        if (_editingPanel is null)
        {
            return;
        }

        if (!RedetectAvailable)
        {
            await ShowToast("Re-detect is switched off for now. It is being reworked");
            return;
        }

        if (_panelEditor?.HasUnsavedChanges == true)
        {
            await ShowToast("Save or discard your hold changes before re-detecting");
            return;
        }

        try
        {
            var plan = await PanelRedetect.PlanAsync(WallId, _editingPanel.Id);
            if (plan.Summary.Any)
            {
                redetectPlan = plan;
            }
            else
            {
                await ShowToast(RedetectOutcomes.NothingToDo(plan.Summary));
            }
        }
        catch (Exception ex)
        {
            await ShowToast($"Re-detect failed: {ex.Message}");
        }
    }

    private async Task ConfirmRedetect()
    {
        if (redetectPlan is not { } plan || redetectBusy)
        {
            return;
        }

        redetectBusy = true;
        try
        {
            var done = await PanelRedetect.ApplyAsync(plan);
            redetectPlan = null;
            await LoadEditingPanelHolds();
            await ShowToast(RedetectOutcomes.Done(done));
        }
        catch (Exception ex)
        {
            redetectPlan = null;
            await ShowToast($"Re-detect failed: {ex.Message}");
        }
        finally
        {
            redetectBusy = false;
        }
    }

    private void CancelRedetect()
    {
        if (!redetectBusy)
        {
            redetectPlan = null;
        }
    }
}
