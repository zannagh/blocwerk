// <copyright file="LookOverCopy.Discarded.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>The words about detections the update discarded: why, how sure the detector was, and the two bulk steps.</summary>
internal static partial class LookOverCopy
{
    /// <summary>The chip of the discarded detections a live hold covers.</summary>
    public const string DuplicatesChip = "Probably duplicates";

    /// <summary>The chip and group name of the discarded detections.</summary>
    public const string DiscardedChip = "Discarded detections";

    /// <summary>The answer that adds the detection to the wall.</summary>
    public const string AddHoldLabel = "Add as a hold";

    /// <summary>The answer that leaves it out for good.</summary>
    public const string NotAHoldLabel = "Not a hold";

    /// <summary>Why the update discarded a detection, in plain words.</summary>
    public static string DiscardReason(string? reason) => reason switch
    {
        nameof(NewHoldDiscardReason.SeenOnNeighbourPanel) => "it is seen on the other panel",
        nameof(NewHoldDiscardReason.UnchangedSinceOldPhoto) => "the spot looked unchanged since the old photo",
        nameof(NewHoldDiscardReason.KnownHoldIn3D) => "the 3D model already has a hold there",
        nameof(NewHoldDiscardReason.OutsideOldPhoto) => "the spot is outside the old photo",
        nameof(NewHoldDiscardReason.OnMarker) => "it sits on a marker",
        nameof(NewHoldDiscardReason.OffWallIn3D) => "in the 3D model the spot is off the wall",
        nameof(NewHoldDiscardReason.InsideCarriedHold) => "it lies inside a hold that is already there (a second outline or a part of it)",
        _ => "the reason was not recorded",
    };

    /// <summary>What a discarded detection's card says: the reason, the detector's confidence and what covers it.</summary>
    public static string DiscardedDetail(LookOverFindingView f)
    {
        var sure = f.Confidence is { } confidence ? $" Detector confidence {confidence:0.00}." : string.Empty;
        var covered = f.Overlap >= LookOverRules.NoOverlapBelow ? $" A hold on the wall covers {f.Overlap:P0} of it." : " No hold on the wall is on this spot.";
        return $"The update detected this and left it out: {DiscardReason(f.DiscardReason)}.{sure}{covered}";
    }

    /// <summary>A discarded detection's line in the queue list beside the deck.</summary>
    public static string DiscardedRow(LookOverFindingView f) => f.Confidence is { } confidence ? $"confidence {confidence:0.00}" : "detection";

    /// <summary>The bulk step that adds every detection likely to be a missed hold.</summary>
    public static string AddAllLabel(int count) => $"Add all {count} with confidence ≥ {LookOverRules.SureDetection:0.0} that overlap no hold";

    /// <summary>What <see cref="AddAllLabel"/> will do, asked before it does.</summary>
    public static string AddAllQuestion(int count) =>
        $"Add {PlainCopy.Plural(count, "detection")} as {(count == 1 ? "a hold" : "holds")}? Each gets its detected outline and a suggested colour and is marked for review. "
        + "A detection that overlaps a hold by then is skipped. Each can be removed again in the hold editor.";

    /// <summary>The bulk step that leaves out every detection a live hold covers.</summary>
    public static string NotHoldsLabel(int count) => $"Not a hold for all {count}";

    /// <summary>What <see cref="NotHoldsLabel"/> will do, asked before it does.</summary>
    public static string NotHoldsQuestion(int count) =>
        $"Leave out {PlainCopy.Plural(count, "detection")} that a hold on the wall already covers? Nothing on the wall changes.";

    /// <summary>What a bulk add did.</summary>
    public static string AddedAll(LookOverBulkResult result) => result.Settled >= result.Asked
        ? $"{PlainCopy.Plural(result.Settled, "hold")} added."
        : $"{PlainCopy.Plural(result.Settled, "hold")} added; {result.Asked - result.Settled} skipped because a hold is on the spot by now or it was answered meanwhile.";
}
