// <copyright file="LookOverCopy.Missing.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>The words of "Holds the update could not find", and of findings the hold editor dealt with.</summary>
internal static partial class LookOverCopy
{
    /// <summary>The section's heading.</summary>
    public const string MissingTitle = "Holds the update could not find";

    /// <summary>What the section is, said once under its heading.</summary>
    public const string MissingHint =
        "These holds were on the wall before the update and were not found on the new photos. They are kept, but not drawn on the wall "
        + "and cannot be chosen for a boulder until somebody has said what became of them.";

    /// <summary>The question asked about each hold.</summary>
    public const string MissingQuestion = "The update could not find this hold on the new photo. Is it still on the wall? If so, where?";

    /// <summary>Why a moderator is not offered "it is this hold".</summary>
    public const string ReassignNeedsAdmin =
        "Tap the wall where the hold is now. Saying that it is one of the holds on the new photo merges two holds, which only a wall admin can do.";

    /// <summary>How to say where the hold is, for somebody who may do both.</summary>
    public const string TapHint = "Tap the hold on the new photo that it is, or tap the bare wall where it is now. The dashed outline is where it was.";

    /// <summary>What "it is this hold" does, asked before it is done.</summary>
    public static string ReassignQuestion(LookOverFindingView f)
    {
        const string Rest = "It keeps its place and outline and takes the old hold's name and settings where it has none. "
            + "A wall admin can undo this under Wall settings, Possible duplicate holds.";
        var count = f.Boulders.Count;
        if (count == 0)
        {
            return $"Is the hold from before the update the one marked green? The old hold is merged into it. {Rest}";
        }

        var s = count == 1 ? "s" : string.Empty;
        return $"Is the hold from before the update the one marked green? The old hold is merged into it: {PlainCopy.Plural(count, "boulder")} "
            + $"({Names(f)}) use{s} the marked hold from now on. {Rest}";
    }

    /// <summary>What "it is here" does, asked before it is done.</summary>
    public static string MoveQuestion(LookOverFindingView f)
    {
        var boulders = f.Boulders.Count == 0 ? string.Empty : $" and its {(f.Boulders.Count == 1 ? "boulder" : "boulders")}";
        return $"Is the hold at the spot marked green now? It is moved there with its outline{boulders}, drawn on the wall again, "
            + "and marked for review so that the outline can be checked in the hold editor.";
    }

    /// <summary>The bulk removal's button, saying how many holds it removes and which.</summary>
    public static string BulkRemoveLabel(int count, bool onlyCovered) => onlyCovered
        ? $"Not there any more: the {PlainCopy.Plural(count, "hold")} under a new hold that no boulder uses"
        : $"Not there any more: all {PlainCopy.Plural(count, "hold")} that no boulder uses";

    /// <summary>What the bulk removal does, asked before it is done.</summary>
    public static string BulkRemoveQuestion(int count, bool onlyCovered)
    {
        var which = onlyCovered ? "lie under a hold the update found on the new photo and are used by no boulder" : "are used by no boulder";
        return $"Remove {PlainCopy.Plural(count, "hold")} from the wall without looking at each? They were not found by the update and {which}. "
            + "No boulder changes. This cannot be undone here.";
    }

    /// <summary>Said after a bulk removal.</summary>
    public static string BulkRemoved(LookOverBulkResult result) => result.Settled >= result.Asked
        ? $"{PlainCopy.Plural(result.Settled, "hold")} removed."
        : $"{result.Settled} of {result.Asked} holds removed; the others were answered meanwhile, or a boulder uses them by now.";

    /// <summary>What was done about a finding in the hold editor instead of here.</summary>
    public static string ClosedLabel(LookOverCloseReason? reason) => reason switch
    {
        LookOverCloseReason.RemovedInEditor => "Hold removed in the hold editor",
        LookOverCloseReason.MergedInEditor => "Hold merged in the duplicate review",
        LookOverCloseReason.HoldAddedHere => "A hold was added here in the hold editor",
        _ => "Hold checked in the hold editor",
    };
}
