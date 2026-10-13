// <copyright file="WallPhotoEditor.ModelActions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The tools that change the wall MODEL (make actual, join, mark changed / unchanged, delete) rather
/// than one hold's drawing: the outcome each one states before it acts, the boulder usage those
/// sentences count, and how the pick is previewed on the photo. What the tools DO is unchanged and
/// still lives next to their handlers in the component.
/// </summary>
public partial class WallPhotoEditor
{
    private const string PreviewStays = "#33dd66";
    private const string PreviewGoes = "#ff5566";
    private const string PreviewHintText = "On the photo: green stays, red dashed disappears.";

    /// <summary>A hold whose delete waits for a yes, because boulders use it.</summary>
    private Guid? pendingDeleteHoldId;

    private HoldActionOutcome? MakeActualOutcome
    {
        get
        {
            if (HoldById(_makeActualVirtualId) is not { } virtualHold)
            {
                return null;
            }

            return HoldById(_makeActualTargetId) is { } target
                ? HoldActionOutcomes.MergeVirtual(virtualHold, target, UsageOrNull(virtualHold.Id), UsageOrNull(target.Id))
                : HoldActionOutcomes.MakeActualAsIs(virtualHold, UsageOrNull(virtualHold.Id));
        }
    }

    private HoldActionOutcome? JoinVirtualOutcome =>
        HoldById(_joinSurvivorId) is { } keep && HoldById(_joinDuplicateId) is { } drop
            ? HoldActionOutcomes.JoinVirtual(keep, drop, UsageOrNull(drop.Id))
            : null;

    private HoldActionOutcome? MarkChangedOutcome =>
        HoldById(_markChangedHoldId) is { } hold ? HoldActionOutcomes.MarkChanged(hold, UsageOrNull(hold.Id)) : null;

    private HoldActionOutcome? MarkUnchangedOutcome =>
        HoldById(_markUnchangedHoldId) is { } hold ? HoldActionOutcomes.MarkUnchanged(hold, UsageOrNull(hold.Id)) : null;

    private HoldActionOutcome? DeleteOutcome =>
        HoldById(pendingDeleteHoldId) is { } hold ? HoldActionOutcomes.Delete(hold, UsageOrNull(hold.Id)) : null;

    private Task ConfirmMakeActual() => _makeActualTargetId is null ? MakeActualAsIs() : ConfirmMakeActualMerge();

    private static bool ChangesModel(HoldTouchupTool tool) => tool is HoldTouchupTool.MakeActual
        or HoldTouchupTool.JoinVirtual or HoldTouchupTool.MarkChanged or HoldTouchupTool.MarkUnchanged;

    private Hold? HoldById(Guid? id) => id is { } value ? Holds.FirstOrDefault(h => h.Id == value) : null;

    private IReadOnlyList<HoldUsageRef>? UsageOrNull(Guid holdId) => _holdUsage is null ? null : UsageFor(holdId);

    /// <summary>
    /// Loads which boulders use which hold, once per tool pick, so an outcome can count and name them.
    /// A host that cannot say (no usage callback) leaves the map empty and the outcome says so.
    /// </summary>
    private async Task EnsureHoldUsageAsync()
    {
        if (_holdUsage is null && OnRequestHoldUsage is not null)
        {
            _holdUsage = await OnRequestHoldUsage();
        }
    }

    /// <summary>
    /// The same, read afresh: the model tools act on the server at once, so a count from before the
    /// previous action would already be wrong for the next pick.
    /// </summary>
    private async Task RefreshHoldUsageAsync()
    {
        if (OnRequestHoldUsage is not null)
        {
            _holdUsage = await OnRequestHoldUsage();
        }
    }

    /// <summary>
    /// A tap with the Delete tool, or the Del button: a hold no current boulder uses goes at once, as
    /// before; one that boulders use first says which boulders become historic.
    /// </summary>
    private async Task RequestDeleteAsync(Guid holdId)
    {
        await EnsureHoldUsageAsync();
        if (UsageFor(holdId).Any(u => !u.IsHistoric))
        {
            pendingDeleteHoldId = holdId;
            return;
        }

        DeleteHold(holdId);
    }

    private void ConfirmDelete()
    {
        if (pendingDeleteHoldId is { } holdId)
        {
            DeleteHold(holdId);
        }
    }

    private void DeleteHold(Guid holdId)
    {
        pendingDeleteHoldId = null;
        TakeSnapshot();
        _deletedHoldIds.Add(holdId);
        Holds.RemoveAll(h => h.Id == holdId);
        if (_selectedHoldId == holdId)
        {
            _selectedHoldId = null;
        }
    }

    /// <summary>
    /// How a hold is drawn while a confirm is open, so the outcome can be seen on the photo: the
    /// outline that remains solid green, the one that disappears dashed red. For a virtual hold merged
    /// into a detected one the DETECTED outline is the one that remains (the hold that stays moves
    /// onto it). Null for every hold the open confirm does not concern.
    /// </summary>
    private (string Stroke, string Dash)? ModelActionPreview(Hold hold)
    {
        if (pendingDeleteHoldId == hold.Id)
        {
            return (PreviewGoes, "0.5 0.35");
        }

        if (_mode == HoldTouchupTool.MakeActual && _makeActualVirtualId is { } virtualId)
        {
            if (_makeActualTargetId is { } targetId)
            {
                return hold.Id == targetId ? (PreviewStays, string.Empty)
                    : hold.Id == virtualId ? (PreviewGoes, "0.5 0.35")
                    : null;
            }

            return hold.Id == virtualId ? (PreviewStays, string.Empty) : null;
        }

        if (_mode == HoldTouchupTool.JoinVirtual && _joinSurvivorId is { } keepId && _joinDuplicateId is { } dropId)
        {
            return hold.Id == keepId ? (PreviewStays, string.Empty)
                : hold.Id == dropId ? (PreviewGoes, "0.5 0.35")
                : null;
        }

        return null;
    }
}
