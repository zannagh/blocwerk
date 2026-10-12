// <copyright file="LookOverCopy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// The words of the look-over: what each finding is, what each answer is called for it, and what the answers that
/// change the wall will do, said before they do it. A card only says what the update did and what it could not tell;
/// whether a hold is really gone is the reader's to see in the pictures. The wall page's notice is in the Banner part.
/// </summary>
internal static partial class LookOverCopy
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
        if (f.Status == LookOverFindingStatus.Open && f.HoldId is null && f.Kind != LookOverFindingKind.ConflictingNew)
        {
            return "This hold is no longer on the wall as the update left it. Nothing is left to decide.";
        }

        var hold = f.HoldName is { } name ? $"the {name} hold" : "this hold";
        return f.Kind switch
        {
            LookOverFindingKind.ReplacedByOther when f.Converted =>
                "You said this is not the hold that was here before. It is marked as changed and the boulders that use it are marked for review. Keep it, or remove it from the wall.",
            LookOverFindingKind.ReplacedByOther =>
                $"To the update, the hold on this spot looks different from {hold} that was here. It kept the old hold in place and added the other one as a new hold.",
            LookOverFindingKind.NotFound => $"The update did not find {hold} again on the new photo. It was kept where it was.",
            LookOverFindingKind.PossiblyRemoved =>
                $"The update did not find {hold} again, and to it the spot looks like bare wall on the new photo and in the 3D model. It was kept where it was.",
            LookOverFindingKind.UnsureMatch => $"The update carried {hold} onto a detection that may not be the same hold. Compare the two photos.",
            LookOverFindingKind.SameSpot => $"The update did not find {hold} again and kept it where it was, and it detected a new hold on the same spot. Both are on the wall now.",
            LookOverFindingKind.MatchedToHandPlaced =>
                "A hold placed by hand was merged onto a detected hold: it kept its boulders and settings and took the detection's position and outline. "
                + "It cannot be split again from here; if the merge is wrong, correct the hold in the hold editor.",
            LookOverFindingKind.HandPlacedAmbiguous =>
                "More than one detection sits on this hand-placed hold. Nothing was merged and the hold stayed as it was. Remove a surplus detection in the hold editor.",
            _ => "The photo check left a detection out here, while the 3D model suggests a hold. Nothing was added. If there is a hold, add it in the hold editor.",
        };
    }

    /// <summary>The name of "it is fine as it is" for this finding.</summary>
    public static string SettleLabel(LookOverFindingView f)
    {
        if (f.Status == LookOverFindingStatus.Open && f.HoldId is null)
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

    /// <summary>The name of the merge: about the boulders when there are any, about the two holds when there are none.</summary>
    public static string MergeLabel(LookOverFindingView f) => f.Boulders.Count == 0 ? "Merge into the new hold" : "Boulders should use the new hold";

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

    /// <summary>What calling an unsure match a different hold does, asked before it is done.</summary>
    public static string DifferentQuestion(LookOverFindingView f)
    {
        var count = f.Boulders.Count;
        var boulders = count == 0
            ? "No current boulder uses it here"
            : $"{PlainCopy.Plural(count, "boulder")} ({Names(f)}) {(count == 1 ? "is" : "are")} marked for review";
        return $"Not the hold that was here before? The hold is marked as changed. {boulders}; "
            + "boulders that use the same hold on a neighbouring panel are marked too. Nothing is removed.";
    }

    /// <summary>What the merge does, asked before it is done; it only talks about boulders when there are any.</summary>
    public static string UseNewQuestion(LookOverFindingView f)
    {
        const string Undo = "The old hold is removed. A wall admin can undo this under Wall settings, Possible duplicate holds.";
        var count = f.Boulders.Count;
        if (count == 0)
        {
            return $"Merge the old hold into the new hold on this spot? {Undo}";
        }

        var s = count == 1 ? "s" : string.Empty;
        return $"Merge the old hold into the new hold on this spot? {PlainCopy.Plural(count, "boulder")} ({Names(f)}) keep{s} the same mark "
            + $"and use{s} the new hold from now on, still marked for review. {Undo}";
    }

    /// <summary>How a settled finding was answered, and by whom.</summary>
    public static string AnsweredLabel(LookOverFindingView f)
    {
        if (f.Status == LookOverFindingStatus.Closed)
        {
            return ClosedLabel(f.ClosedReason);
        }

        var what = f.Answer switch
        {
            LookOverAnswer.Reassign => "Is a hold on the new photo",
            LookOverAnswer.Moved => "Moved to where it is",
            LookOverAnswer.RemoveHold => "Hold removed",
            LookOverAnswer.UseNewHold => "Merged into the new hold",
            _ => f.KeptAtUpdate ? "Still here" : SettleLabel(f),
        };
        return f.AnsweredBy is { } who ? $"{what} · {who}" : what;
    }

    /// <summary>What the answer did, in one line under the card; null when it did nothing worth saying.</summary>
    public static string? ResultLine(LookOverFindingView f)
    {
        var boulders = f.AnswerBoulders ?? 0;
        return f.Answer switch
        {
            LookOverAnswer.RemoveHold when boulders == 0 => "Hold removed; no boulder used it.",
            LookOverAnswer.RemoveHold => $"Hold removed; {PlainCopy.Plural(boulders, "boulder")} {(boulders == 1 ? "is" : "are")} now historic.",
            LookOverAnswer.UseNewHold or LookOverAnswer.Reassign => "Merged into the new hold. Undo under Wall settings › Possible duplicate holds.",
            LookOverAnswer.Moved => "Marked for review: check its outline in the hold editor.",
            LookOverAnswer.Settle when f.ClearedBoulders > 0 =>
                $"{PlainCopy.Plural(f.ClearedBoulders, "boulder")} {(f.ClearedBoulders == 1 ? "is" : "are")} no longer marked for review.",
            _ => null,
        };
    }

    private static string Names(LookOverFindingView f)
    {
        const int Max = 4;
        var names = string.Join(", ", f.Boulders.Take(Max).Select(b => b.Name));
        return f.Boulders.Count > Max ? $"{names} and {f.Boulders.Count - Max} more" : names;
    }
}
