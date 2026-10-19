// <copyright file="PanelOverlapStepper.Session.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
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
    private const string TwinReleasedHint =
        "An old hold was matched to that hold. It keeps its choice and is carried at its old place; check it in the look-over.";

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

    /// <summary>
    /// Deletes a staged hold in the session; false (with the reason shown) when it was refused. The bin is the user's
    /// explicit choice and the carry-over review only shows the centre, so a match an old hold had to this hold is
    /// released (the old hold keeps its verdict, without the match) rather than refused with nowhere to go. The user is
    /// told when that happened.
    /// </summary>
    private async Task<bool> TryDeleteStagedInSessionAsync(Guid id)
    {
        try
        {
            var released = await WallPanelService.DeleteStagedHoldInSessionAsync(WallId, SessionId, id, StagedTwinDeletion.ReleaseTwin);
            if (released > 0)
            {
                _warning = TwinReleasedHint;
            }

            return true;
        }
        catch (UserFacingException ex)
        {
            _warning = ex.Message;
            return false;
        }
    }
}
