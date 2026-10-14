// <copyright file="WallDetail.Redetect.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Redetect;
using Blocwerk.Web.Components.Shared;
using Blocwerk.Web.Components.Shared.Redetect;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Re-detect on the panel being edited: its own photo and its own holds. The detector runs first and changes nothing;
/// what it would do is shown as proposals a person accepts or leaves one by one (<see cref="RedetectReview"/>), and
/// only the accepted ones are applied. Never with unsaved hold edits pending, which would be judged against holds that
/// are not stored yet.
/// </summary>
public partial class WallDetail
{
    /// <summary>
    /// Off unless the installation switches it on (<see cref="HoldDetectionSettings.RedetectEnabled"/>): on a real wall
    /// one press of the old re-detect added hundreds of holds on markers, mats and spots answered "Not a hold" (see
    /// issue #127). It now only proposes, and stays off on the live site until its proposals were checked.
    /// </summary>
    private bool RedetectAvailable => Settings.HoldDetection.RedetectEnabled;

    private PanelRedetectPlan? redetectPlan;
    private bool redetectBusy;
    private bool redetectPlanning;

    [Inject]
    private IPanelRedetectService PanelRedetect { get; set; } = null!;

    [Inject]
    private BlocwerkSettings Settings { get; set; } = null!;

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

        if (redetectPlanning)
        {
            await ShowToast("Re-detect is still looking at the photo");
            return;
        }

        redetectPlanning = true;
        try
        {
            // Detecting and outlining a panel photo takes a minute or two; the page stays usable meanwhile.
            await ShowToast("Re-detect is looking at the photo. This takes a minute or two, the proposals open when it is done");
            var plan = await PanelRedetect.PlanAsync(WallId, _editingPanel.Id);
            if (plan.Proposals.Count > 0)
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
        finally
        {
            redetectPlanning = false;
        }
    }

    private async Task ConfirmRedetect(IReadOnlySet<string> accepted)
    {
        if (redetectPlan is not { } plan || redetectBusy)
        {
            return;
        }

        redetectBusy = true;
        try
        {
            var done = await PanelRedetect.ApplyAsync(plan, accepted);
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
