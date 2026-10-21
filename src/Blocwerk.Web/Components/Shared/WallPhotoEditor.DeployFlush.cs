// <copyright file="WallPhotoEditor.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.State;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The pre-deploy flush of the hold editor (see <see cref="CircuitEditFlush"/>), driven by the page that hosts it.
/// </summary>
public partial class WallPhotoEditor
{
    /// <summary>
    /// Saves the pending batch (adds, moves, deletes, "Mark as changed" drafts, alignment nudges) exactly as
    /// "Save Changes" / Ctrl+S would. A manual alignment in progress is NOT confirmed on the user's behalf: "Confirm &amp;
    /// Update Wall" re-projects the whole wall, so it is reported as not saved and the user is told.
    /// </summary>
    public async Task<EditFlushOutcome> FlushForDeployAsync()
    {
        var action = EditorFlushPolicy.WallEditor(
            ManualAlignment, _hasPendingAlignmentWork, CanEdit, _pendingChanges > 0 || _alignmentChangedIds.Count > 0);
        if (action != EditFlushAction.SaveOnServer)
        {
            return action == EditFlushAction.CannotKeep ? EditFlushOutcome.NotSaved : EditFlushOutcome.NothingToSave;
        }

        await SaveChanges();
        StateHasChanged();
        return _pendingChanges == 0 && _alignmentChangedIds.Count == 0
            ? EditFlushOutcome.SavedOnServer
            : EditFlushOutcome.NotSaved;
    }
}
