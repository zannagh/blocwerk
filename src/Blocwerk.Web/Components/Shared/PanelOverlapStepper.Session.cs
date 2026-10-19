// <copyright file="PanelOverlapStepper.Session.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The stepper inside a wall update: its staged-hold edits name the update session the wizard is looking at (a tab
/// left on a replaced update must not edit the update that replaced it), a delete never takes an old hold's carry
/// verdict with it, and a finish the wizard could not save hands the panel back instead of hanging on "Adding panel…".
/// </summary>
public partial class PanelOverlapStepper
{
    private const string TwinDeleteHint =
        "This hold is where an old hold was matched to in the carry-over review. To drop it, change that hold's verdict there.";

    /// <summary>The wall update session the edits belong to; null outside a wall update (adding a panel).</summary>
    [Parameter]
    public Guid? SessionId { get; set; }

    /// <summary>The wizard could not save this panel's outcome: show the panel again so it can be finished again.</summary>
    public void FinishFailed()
    {
        _finishing = false;
        StateHasChanged();
    }

    /// <summary>Moves or resizes a staged hold in the session; false (with the reason shown) when it was refused.</summary>
    private async Task<bool> TryMoveStagedInSessionAsync(PanelImageView.HoldGeometry g)
    {
        try
        {
            await WallPanelService.UpdateStagedHoldInSessionAsync(WallId, SessionId, g.HoldId, g.X, g.Y, g.Radius);
            return true;
        }
        catch (UserFacingException ex)
        {
            _warning = ex.Message;
            return false;
        }
    }

    /// <summary>Deletes a staged hold in the session; false (with the reason shown) when it was refused.</summary>
    private async Task<bool> TryDeleteStagedInSessionAsync(Guid id)
    {
        try
        {
            await WallPanelService.DeleteStagedHoldInSessionAsync(WallId, SessionId, id);
            return true;
        }
        catch (StagedHoldIsTwinException)
        {
            _warning = TwinDeleteHint;
            return false;
        }
        catch (UserFacingException ex)
        {
            _warning = ex.Message;
            return false;
        }
    }
}
