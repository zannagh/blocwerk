// <copyright file="LookOverCopy.NewHold.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>The words of "use the new hold" asked per boulder: the question, the confirm button and what it did.</summary>
internal static partial class LookOverCopy
{
    /// <summary>The question asked once per boulder of a replaced hold.</summary>
    public const string NewHoldPerBoulderQuestion = "Should this boulder use the new hold in that place?";

    private const string MergeUndo = "Undo under Wall settings › Possible duplicate holds.";

    /// <summary>The confirm button of the merge, after the per-boulder answers.</summary>
    /// <param name="f">The finding.</param>
    /// <param name="declined">How many boulders were answered no.</param>
    /// <returns>The label.</returns>
    public static string UseNewConfirmLabel(LookOverFindingView f, int declined)
    {
        if (declined == 0)
        {
            return "Merge into the new hold";
        }

        return declined >= f.Boulders.Count ? "Remove the old hold" : $"Move {f.Boulders.Count - declined}, retire {declined}";
    }

    private static string NewHoldAllResult(int cleared) =>
        cleared == 0
            ? $"Merged into the new hold. {MergeUndo}"
            : $"Merged into the new hold; {PlainCopy.Plural(cleared, "boulder")} {(cleared == 1 ? "is" : "are")} no longer marked for review. {MergeUndo}";

    private static string NewHoldSomeResult(int moved, int historic, int cleared)
    {
        var line = $"{PlainCopy.Plural(moved, "boulder")} use{(moved == 1 ? "s" : string.Empty)} the new hold; "
            + $"{PlainCopy.Plural(historic, "boulder")} {(historic == 1 ? "is" : "are")} now historic and the old hold is removed.";
        var marks = cleared == 0 ? string.Empty : $" {PlainCopy.Plural(cleared, "boulder")} no longer marked for review.";
        return $"{line}{marks} {MergeUndo}";
    }
}
