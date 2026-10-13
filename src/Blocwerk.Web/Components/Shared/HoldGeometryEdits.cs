// <copyright file="HoldGeometryEdits.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The geometry edits the wall editor applies to its working copy of a hold before a save: the move a
/// finished drag reports and the reset of an outline back to a circle. Kept apart from the component so
/// the rules can be tested without a browser.
/// </summary>
public static class HoldGeometryEdits
{
    /// <summary>
    /// Moves the hold by a drag's distance, both in the photo's normalized 0..1 space, and keeps the centre
    /// on the photo. The outline moves with it: its points are offsets from the centre.
    /// </summary>
    /// <param name="hold">The hold to move.</param>
    /// <param name="dx">Distance along X.</param>
    /// <param name="dy">Distance along Y.</param>
    /// <returns>False when the distance is not a number or the hold did not move; the hold is then untouched.</returns>
    public static bool TryMoveBy(Hold hold, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            return false;
        }

        var x = Math.Clamp(hold.X + dx, 0, 1);
        var y = Math.Clamp(hold.Y + dy, 0, 1);
        if (Math.Abs(x - hold.X) < 1e-9 && Math.Abs(y - hold.Y) < 1e-9)
        {
            return false;
        }

        hold.X = x;
        hold.Y = y;
        return true;
    }

    /// <summary>
    /// Moves a hold and, with it, every hold that sits on it (at any depth): they keep their place on it. The hold
    /// stays on the photo, and what sits on it moves by the distance the hold really moved. Their sizes do not change.
    /// </summary>
    /// <param name="holds">The holds of the photo.</param>
    /// <param name="hold">The hold that was dragged.</param>
    /// <param name="dx">Distance along X.</param>
    /// <param name="dy">Distance along Y.</param>
    /// <returns>The holds that moved, the dragged one first; empty when it did not move.</returns>
    public static List<Hold> MoveWithStack(IReadOnlyList<Hold> holds, Hold hold, double dx, double dy)
    {
        var (fromX, fromY) = (hold.X, hold.Y);
        if (!TryMoveBy(hold, dx, dy))
        {
            return [];
        }

        var on = holds.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First().StackedOnHoldId);
        var above = HoldStackOrder.Above(on, hold.Id).ToHashSet();
        var moved = new List<Hold> { hold };
        moved.AddRange(holds.Where(h => above.Contains(h.Id) && TryMoveBy(h, hold.X - fromX, hold.Y - fromY)));
        return moved;
    }

    /// <summary>Whether the hold is drawn from an outline (drawn by hand or detected) instead of as a plain circle.</summary>
    /// <param name="hold">The hold to look at.</param>
    /// <returns>True when there is an outline to reset.</returns>
    public static bool HasOutline(Hold hold) => hold.ShapePoints is { Count: > 0 } || hold.ShapeHoles is { Count: > 0 };

    /// <summary>
    /// Replaces the hold's outline, and any pocket holes in it, with the plain circle of its current radius
    /// at its centre. The save has to say so explicitly (<c>HoldEdit.FromEditorState</c>'s
    /// <c>resetToCircle</c>): a missing outline alone means "leave it as it is".
    /// </summary>
    /// <param name="hold">The hold to reset.</param>
    /// <returns>False when the hold already is a plain circle.</returns>
    public static bool ResetToCircle(Hold hold)
    {
        if (!HasOutline(hold))
        {
            return false;
        }

        hold.ShapePoints = null;
        hold.ShapeControlPoints = null;
        hold.ShapeHoles = null;
        return true;
    }
}
