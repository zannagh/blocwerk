// <copyright file="WallPhotoEditor.Stack.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds;
using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Holds that sit on holds (<see cref="Hold.StackedOnHoldId"/>) in the wall editor: the overlay draws a base before
/// what sits on it, so the upper hold is the one a tap meets; the Move tool says what the selected hold sits on and
/// lets it be put on another hold or back on the wall; and a base that is moved takes its holds with it. All of it
/// edits the working copy like every other edit: Discard takes it back, the save writes it.
/// </summary>
public partial class WallPhotoEditor
{
    /// <summary>The holds whose base was changed here since the last save: only those send it.</summary>
    private readonly HashSet<Guid> restackedIds = [];

    private HoldStackMemo? stackMemo;

    /// <summary>The hold waiting for a tap on the hold it sits on.</summary>
    private Guid? stackPickHoldId;

    /// <summary>Why the last tap was not taken as the base, shown until the next one.</summary>
    private string? stackPickRefusal;

    /// <summary>Offered where a save can write it: the same editors that can reset an outline.</summary>
    private bool CanStack => CanResetToCircle;

    /// <summary>The working copy with every base before what sits on it: the order the overlay draws in.</summary>
    private List<Hold> HoldsBackToFront => StackMemo().Order;

    private static string StackLabel(Hold hold)
    {
        if (!string.IsNullOrWhiteSpace(hold.Name))
        {
            return hold.Name;
        }

        return string.IsNullOrWhiteSpace(hold.Color) ? "a hold without a colour" : $"the {HoldPalette.DisplayName(hold.Color).ToLowerInvariant()} hold";
    }

    /// <summary>Hold id to the id of the hold it sits on. Read-only: it is shared between calls until a hold changes.</summary>
    private Dictionary<Guid, Guid?> StackMap() => StackMemo().On;

    /// <summary>The stack map and the drawing order, computed again only when a hold or a base changed.</summary>
    private HoldStackMemo StackMemo()
    {
        if (stackMemo is null || !stackMemo.Matches(Holds))
        {
            stackMemo = HoldStackMemo.For(Holds);
        }

        return stackMemo;
    }

    private Hold? BaseOf(Hold hold) => hold.StackedOnHoldId is { } id ? Holds.FirstOrDefault(h => h.Id == id) : null;

    /// <summary>The ids of the holds a hold sits on, for <c>data-hold-on</c>: wall-hold-drag.js moves it along with any of them.</summary>
    private static string? StackKeys(Dictionary<Guid, Guid?> on, Hold hold) =>
        hold.StackedOnHoldId is null ? null : string.Join(' ', HoldStackOrder.Below(on, hold.Id)) is { Length: > 0 } keys ? keys : null;

    private void StartStackPick(Hold hold)
    {
        stackPickHoldId = hold.Id;
        stackPickRefusal = null;
    }

    private void CancelStackPick()
    {
        stackPickHoldId = null;
        stackPickRefusal = null;
    }

    /// <summary>While a hold waits for its base, a tap on another hold is that base. True when the tap was used up.</summary>
    private bool TryPickBase(Hold tapped)
    {
        if (stackPickHoldId is not { } id || _mode != HoldTouchupTool.Move || Holds.FirstOrDefault(h => h.Id == id) is not { } upper)
        {
            stackPickHoldId = null;
            return false;
        }

        _selectedHoldId = upper.Id;
        _selectedRadius = upper.Radius;
        if (HoldStackOrder.WouldLoop(StackMap(), upper.Id, tapped.Id))
        {
            stackPickRefusal = tapped.Id == upper.Id ? "Tap another hold." : "That hold sits on this one. Tap another hold.";
            return true;
        }

        TakeSnapshot();
        upper.StackedOnHoldId = tapped.Id;
        MarkRestacked(upper);
        CancelStackPick();
        return true;
    }

    private void TakeOffBase(Hold hold)
    {
        TakeSnapshot();
        hold.StackedOnHoldId = null;
        MarkRestacked(hold);
    }

    private void MarkRestacked(Hold hold)
    {
        restackedIds.Add(hold.Id);
        TrackUpdate(hold);
    }
}
