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
    /// Saves the pending batch through the same latch as "Save Changes" / Ctrl+S, in the scope
    /// <see cref="EditorFlushPolicy.WallFlush"/> sets. A manual alignment in progress is NOT confirmed on the user's
    /// behalf: "Confirm &amp; Update Wall" re-projects the whole wall, so it is reported as not saved.
    /// </summary>
    public async Task<WallFlushResult> FlushForDeployAsync()
    {
        var action = EditorFlushPolicy.WallEditor(
            ManualAlignment, _hasPendingAlignmentWork, CanEdit, _pendingChanges > 0 || _alignmentChangedIds.Count > 0, _isSavingBatch);
        if (action != EditFlushAction.SaveOnServer)
        {
            return WallFlushResult.Plain(action == EditFlushAction.CannotKeep ? EditFlushOutcome.NotSaved : EditFlushOutcome.NothingToSave);
        }

        var everything = EditorFlushPolicy.WallFlush == WallFlushScope.Everything;
        var (retired, flagged) = everything ? await AffectedByDestructiveAsync() : ([], []);
        if (!await SaveBatchAsync(includeDestructive: everything))
        {
            // The user's own save is in flight; it carries the batch.
            return WallFlushResult.Plain(EditFlushOutcome.NothingToSave);
        }

        StateHasChanged();
        var unsent = _addedHolds.Count + _updatedHoldIds.Count + _alignmentChangedIds.Count;
        return new WallFlushResult(
            unsent == 0 ? EditFlushOutcome.SavedOnServer : EditFlushOutcome.NotSaved,
            [.. _deletedHoldIds],
            [.. _markChangedHoldIds],
            retired,
            flagged);
    }

    /// <summary>
    /// Re-drafts deletions and "Mark as changed" decisions kept on the device by an adds-and-moves-only flush, for the
    /// holds this editor shows; returns the ones it could not place (another panel), for the page to keep again.
    /// </summary>
    public (List<Guid> Deleted, List<Guid> Marked) RedraftKeptDecisions(IEnumerable<Guid> deleted, IEnumerable<Guid> marked)
    {
        var shown = Holds.Select(h => h.Id).ToHashSet();
        var leftDeleted = new List<Guid>();
        foreach (var id in deleted)
        {
            if (shown.Contains(id))
            {
                DeleteHold(id);
            }
            else
            {
                leftDeleted.Add(id);
            }
        }

        var leftMarked = marked.Where(id => !shown.Contains(id)).ToList();
        _markChangedHoldIds.UnionWith(marked.Where(shown.Contains));
        StateHasChanged();
        return (leftDeleted, leftMarked);
    }

    /// <summary>
    /// Which live boulders the pending deletions will retire and which the pending "Mark as changed" will flag, read
    /// afresh from the server (it needs no browser) right before the save, as the Delete tool's own confirm does.
    /// </summary>
    private async Task<(List<AffectedBoulder> Retired, List<AffectedBoulder> Flagged)> AffectedByDestructiveAsync()
    {
        if (_deletedHoldIds.Count == 0 && _markChangedHoldIds.Count == 0)
        {
            return ([], []);
        }

        await RefreshHoldUsageAsync();
        return EditorFlushPolicy.AffectedBoulders(_deletedHoldIds, _markChangedHoldIds, UsageFor);
    }
}
