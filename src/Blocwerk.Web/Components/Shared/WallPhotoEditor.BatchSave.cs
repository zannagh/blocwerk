// <copyright file="WallPhotoEditor.BatchSave.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Saving the pending hold batch: the "Save Changes" button, Ctrl+S and the pre-deploy flush all come through
/// <see cref="SaveBatchAsync"/>, behind one latch.
/// </summary>
public partial class WallPhotoEditor
{
    // Set before the first await of a batch save and cleared after it. Every caller respects it, so one batch can
    // never be sent twice: a second send while the first is in flight would add its new holds again.
    private bool _isSavingBatch;

    private Task SaveChanges() => SaveBatchAsync(includeDestructive: true);

    /// <summary>
    /// Sends the pending batch. <paramref name="includeDestructive"/> false leaves deletions and "Mark as changed"
    /// drafts pending (the pre-deploy flush's adds-and-moves-only scope). Returns false when a save was already under way.
    /// Only what was sent is forgotten afterwards, so an edit made while the save was in flight stays pending.
    /// </summary>
    private async Task<bool> SaveBatchAsync(bool includeDestructive)
    {
        if (_isSavingBatch)
        {
            return false;
        }

        _isSavingBatch = true;
        try
        {
            var added = _addedHolds.ToList();
            var addedIds = added.Select(h => h.Id).ToHashSet();
            var touched = new HashSet<Guid>(_updatedHoldIds);
            touched.UnionWith(_alignmentChangedIds);
            var updated = Holds.Where(h => touched.Contains(h.Id) && !addedIds.Contains(h.Id)).ToList();
            List<Guid> deleted = includeDestructive ? [.. _deletedHoldIds] : [];
            List<Guid> markedChanged = includeDestructive ? [.. _markChangedHoldIds] : [];

            await SendBatchAsync(added, updated, deleted, markedChanged);

            _addedHolds.RemoveAll(added.Contains);
            touched.UnionWith(addedIds);
            _updatedHoldIds.ExceptWith(touched);
            _alignmentChangedIds.ExceptWith(touched);
            circleResetIds.ExceptWith(touched);
            restackedIds.ExceptWith(touched);
            _deletedHoldIds.ExceptWith(deleted);
            _markChangedHoldIds.ExceptWith(markedChanged);
            if (_pendingChanges == 0 && _alignmentChangedIds.Count == 0)
            {
                _snapshotHolds = null;
            }

            return true;
        }
        finally
        {
            _isSavingBatch = false;
        }
    }

    private async Task SendBatchAsync(List<Hold> added, List<Hold> updated, List<Guid> deleted, List<Guid> markedChanged)
    {
        if (OnBatchSave.HasDelegate)
        {
            // Only a hold that still has no outline is a reset: one redrawn afterwards saves its new outline.
            var resetToCircle = updated.Where(h => circleResetIds.Contains(h.Id) && h.ShapePoints == null).Select(h => h.Id).ToHashSet();
            var restacked = updated.Select(h => h.Id).Where(restackedIds.Contains).ToHashSet();
            await OnBatchSave.InvokeAsync(new BatchChanges(added, updated, deleted, markedChanged) { ResetToCircle = resetToCircle, Restacked = restacked });
            return;
        }

        foreach (var h in added)
        {
            await OnHoldAdded.InvokeAsync(h);
        }

        foreach (var h in updated)
        {
            await OnHoldUpdated.InvokeAsync(h);
        }

        foreach (var id in deleted)
        {
            await OnHoldDeleted.InvokeAsync(id);
        }

        // The mark goes through OnMarkHoldModified, never through the hold write, so a marked hold's peripheral and
        // overlap twins are flagged the same way whichever surface asked for it. Geometry first: the flag describes
        // the hold as saved.
        foreach (var id in markedChanged)
        {
            await OnMarkHoldModified.InvokeAsync(id);
        }
    }
}
