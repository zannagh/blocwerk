// <copyright file="TouchupStep.Guard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The touch-up's edits inside a wall update: each names the update session the wizard is looking at, so a tab left on
/// a replaced update cannot edit the one that replaced it; and deleting a hold that an old hold was matched to asks
/// first. The final touch-up runs AFTER the carryover, and the delete used to take the old hold's verdict (a
/// "Changed", a sign-off) with it, unseen; now the user chooses to keep the verdict without that match, or not to
/// delete.
/// </summary>
public partial class TouchupStep
{
    /// <summary>The wall update session the edits belong to; null for a legacy staged update.</summary>
    [Parameter]
    public Guid? SessionId { get; set; }

    /// <summary>The hold whose delete waits for the user's choice, with the reason; null when nothing waits.</summary>
    private (Guid HoldId, string Reason)? pendingTwinDelete;

    /// <summary>Why the last edit was not saved, shown above the editor; null when it was.</summary>
    private string? editError;

    /// <summary>Deletes a staged hold, asking first when an old hold was matched to it.</summary>
    private async Task DeleteGuardedAsync(Guid id, StagedTwinDeletion twins)
    {
        editError = null;
        try
        {
            await WallPanelService.DeleteStagedHoldInSessionAsync(WallId, SessionId, id, twins);
        }
        catch (StagedHoldIsTwinException ex)
        {
            pendingTwinDelete = (id, ex.Message);
            return;
        }
        catch (UserFacingException ex)
        {
            editError = ex.Message;
            return;
        }

        pendingTwinDelete = null;
        _touchup.Removed(id);
        await ReloadHoldsAsync();
    }

    /// <summary>The user chose to delete the matched hold: the old holds keep their verdicts, carried at their old places.</summary>
    private async Task ConfirmTwinDeleteAsync()
    {
        if (pendingTwinDelete is { } pending)
        {
            await DeleteGuardedAsync(pending.HoldId, StagedTwinDeletion.ReleaseTwin);
        }
    }

    private void CancelTwinDelete() => pendingTwinDelete = null;

    /// <summary>Runs one staged edit, showing a refusal instead of letting it escape the event handler.</summary>
    private async Task<bool> TryEditAsync(Func<Task> edit)
    {
        editError = null;
        try
        {
            await edit();
            return true;
        }
        catch (UserFacingException ex)
        {
            editError = ex.Message;
            return false;
        }
    }
}
