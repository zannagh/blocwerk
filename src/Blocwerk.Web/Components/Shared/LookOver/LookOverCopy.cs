// <copyright file="LookOverCopy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// The words of the look-over page: what each finding is, what each answer is called for it, and what the two answers
/// that change the wall will do, said before they do it.
/// </summary>
internal static class LookOverCopy
{
    /// <summary>The heading of a group of findings.</summary>
    public static string GroupTitle(LookOverFindingKind kind) => kind switch
    {
        LookOverFindingKind.ReplacedByOther => "Another hold is here now",
        LookOverFindingKind.NotFound => "Not found again",
        LookOverFindingKind.PossiblyRemoved => "Possibly removed",
        LookOverFindingKind.UnsureMatch => "Unsure matches",
        LookOverFindingKind.SameSpot => "Old and new hold on the same spot",
        LookOverFindingKind.MatchedToHandPlaced => "Hand-placed holds merged onto a detection",
        LookOverFindingKind.HandPlacedAmbiguous => "Hand-placed holds with several detections",
        _ => "New holds with conflicting evidence",
    };

    /// <summary>The heading of one finding.</summary>
    public static string Heading(LookOverFindingView f) => f.Kind switch
    {
        LookOverFindingKind.ReplacedByOther => f.Converted ? "Different hold on this spot" : "Another hold is here now",
        LookOverFindingKind.NotFound => "Not found again",
        LookOverFindingKind.PossiblyRemoved => "Possibly removed",
        LookOverFindingKind.UnsureMatch => "Unsure match",
        LookOverFindingKind.SameSpot => "Old and new hold on the same spot",
        LookOverFindingKind.MatchedToHandPlaced => "Matched to hand-placed hold (now detected)",
        LookOverFindingKind.HandPlacedAmbiguous => "Hand-placed hold, several detections",
        _ => "New hold, conflicting evidence",
    };

    /// <summary>What the update did about the hold, and what is asked now.</summary>
    public static string Detail(LookOverFindingView f)
    {
        if (f.HoldId is null && f.Kind != LookOverFindingKind.ConflictingNew)
        {
            return "This hold has been removed from the wall since. Nothing is left to decide.";
        }

        var hold = f.HoldName is { } name ? $"the {name} hold" : "this hold";
        return f.Kind switch
        {
            LookOverFindingKind.ReplacedByOther when f.Converted =>
                "You said this is not the hold that was here before. It is marked as changed and the boulders that use it are marked for review. Keep it, or remove it from the wall.",
            LookOverFindingKind.ReplacedByOther =>
                $"A different hold now sits where {hold} was. The update kept the old hold in place and added the other one as a new hold.",
            LookOverFindingKind.NotFound => $"The update did not find {hold} again, and its spot no longer looks like it. It was kept where it was.",
            LookOverFindingKind.PossiblyRemoved => $"The new photo and the 3D texture show bare wall where {hold} was. It was kept where it was.",
            LookOverFindingKind.UnsureMatch => $"The update carried {hold} onto a detection that may not be the same hold. Compare the old and the new photo.",
            LookOverFindingKind.SameSpot => $"The update did not find {hold} again and kept it where it was, and a new hold was detected on the same spot. One of the two is probably one too many.",
            LookOverFindingKind.MatchedToHandPlaced =>
                "A hold placed by hand was merged onto a detected hold: it kept its boulders and settings and took the detection's position and outline. "
                + "It cannot be split again from here; if the merge is wrong, correct the hold in the hold editor.",
            LookOverFindingKind.HandPlacedAmbiguous =>
                "More than one detection sits on this hand-placed hold. Nothing was merged and the hold stayed as it was. Remove a surplus detection in the hold editor.",
            _ => "The photo check left a detection out here, but the 3D model sees a hold. Nothing was added. If there is a hold, add it in the hold editor.",
        };
    }

    /// <summary>The name of "it is fine as it is" for this finding.</summary>
    public static string SettleLabel(LookOverFindingView f)
    {
        if (f.HoldId is null)
        {
            return "Understood";
        }

        return f.Kind switch
        {
            LookOverFindingKind.UnsureMatch => "Same hold",
            LookOverFindingKind.ReplacedByOther => f.Converted ? "Keep this hold" : "Keep old hold here",
            LookOverFindingKind.NotFound => "Still there",
            LookOverFindingKind.PossiblyRemoved => "Keep hold",
            LookOverFindingKind.SameSpot => "Both are there",
            LookOverFindingKind.MatchedToHandPlaced => "Merge is right",
            _ => "Understood",
        };
    }

    /// <summary>The name of "remove the hold" for this finding.</summary>
    public static string RemoveLabel(LookOverFindingView f) => f.Kind switch
    {
        LookOverFindingKind.NotFound => "Gone",
        LookOverFindingKind.ReplacedByOther when !f.Converted => "Old hold is gone",
        LookOverFindingKind.SameSpot => "Old hold is gone",
        _ => "Remove hold",
    };

    /// <summary>What removing the hold does to its boulders, asked before it is done.</summary>
    public static string RemoveQuestion(LookOverFindingView f)
    {
        var count = f.Boulders.Count;
        if (count == 0)
        {
            return "Remove this hold from the wall? No current boulder uses it. This cannot be undone here.";
        }

        var (them, become) = count == 1 ? ("it", "it becomes") : ("them", "they become");
        return $"Remove this hold from the wall? {PlainCopy.Plural(count, "boulder")} use{(count == 1 ? "s" : string.Empty)} it ({Names(f)}). "
            + $"The hold is taken off {them} and {become} historic: kept for the record, no longer among the current boulders. This cannot be undone here.";
    }

    /// <summary>What moving the boulders onto the new hold does, asked before it is done.</summary>
    public static string UseNewQuestion(LookOverFindingView f)
    {
        var count = f.Boulders.Count;
        var boulders = count == 0
            ? "No current boulder uses the old hold."
            : $"{PlainCopy.Plural(count, "boulder")} ({Names(f)}) keep{(count == 1 ? "s" : string.Empty)} the same mark and use{(count == 1 ? "s" : string.Empty)} the new hold from now on, still marked for review.";
        return $"Merge the old hold into the new hold on this spot? {boulders} The old hold is removed. "
            + "A wall admin can undo this under Wall settings, Possible duplicate holds.";
    }

    /// <summary>How a settled finding was answered.</summary>
    public static string AnsweredLabel(LookOverFindingView f) => f.Answer switch
    {
        LookOverAnswer.RemoveHold => "Hold removed",
        LookOverAnswer.UseNewHold => "Boulders use the new hold",
        _ => SettleLabel(f),
    };

    private static string Names(LookOverFindingView f)
    {
        const int Max = 4;
        var names = string.Join(", ", f.Boulders.Take(Max).Select(b => b.Name));
        return f.Boulders.Count > Max ? $"{names} and {f.Boulders.Count - Max} more" : names;
    }
}
