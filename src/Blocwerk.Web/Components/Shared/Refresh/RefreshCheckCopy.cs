// <copyright file="RefreshCheckCopy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>The words of the check step's cards: what each card is, and what its two answers are called. One place for the deck and the list.</summary>
internal static class RefreshCheckCopy
{
    /// <summary>Said once about the cards: leaving one alone decides nothing.</summary>
    public const string LeftAlone = "Nothing here is decided for you: if you leave a card alone, the old hold stays and is marked for review.";

    /// <summary>The heading of a card.</summary>
    public static string Heading(RefreshCheck check) => check.Kind switch
    {
        UpdateExceptionKind.PossiblyRemoved => "Possibly removed",
        UpdateExceptionKind.ReplacedByOther => "Another hold is here now",
        UpdateExceptionKind.LowConfidenceMatch => "Unsure match",
        UpdateExceptionKind.MatchedToHandPlaced => "Matched to hand-placed hold (now detected)",
        UpdateExceptionKind.HandPlacedAmbiguous => "Hand-placed hold, several detections",
        _ => "New hold, conflicting evidence",
    };

    /// <summary>What the update saw, and what is asked.</summary>
    public static string Detail(RefreshCheck check) => check.Kind switch
    {
        UpdateExceptionKind.PossiblyRemoved =>
            "The new photo and the 3D texture show bare wall where " + (check.HoldName is { } name ? $"the {name} hold" : "this hold") + " was."
            + (check.Boulders > 0 ? $" {check.Boulders} boulders use it." : string.Empty),
        UpdateExceptionKind.ReplacedByOther =>
            "A different hold now sits where " + (check.HoldName is { } replaced ? $"the {replaced} hold" : "this hold") + " was. The old hold is kept where it was and the other one is added as a new hold."
            + (check.Boulders > 0 ? $" {check.Boulders} boulders use the old hold and are marked for review." : string.Empty)
            + " If it is the same hold after all, pair the two in the full review.",
        UpdateExceptionKind.LowConfidenceMatch => "This old hold was carried onto a detection that may not be the same hold: the match is unsure, or it looks different on the new photo.",
        UpdateExceptionKind.MatchedToHandPlaced =>
            (check.HoldName is { } n ? $"The {n} hold" : "A hold you placed by hand") + " now sits on a detected hold. It keeps its boulders and settings and takes the detection's position and outline."
            + (check.Boulders > 0 ? $" {check.Boulders} boulders use it." : string.Empty),
        UpdateExceptionKind.HandPlacedAmbiguous => "More than one detection sits on this hand-placed hold, or one detection overlaps several. Nothing was merged: the hold stays as it is, marked for review.",
        _ => "The photo check leaves this detection out, but the 3D model sees a hold there.",
    };

    /// <summary>The name of the answer that keeps things as the update proposes.</summary>
    public static string KeepLabel(RefreshCheck check) => check.Kind switch
    {
        UpdateExceptionKind.PossiblyRemoved or UpdateExceptionKind.ReplacedByOther => "Keep hold",
        UpdateExceptionKind.LowConfidenceMatch => "Match is right",
        UpdateExceptionKind.MatchedToHandPlaced => "Merge is right",
        UpdateExceptionKind.HandPlacedAmbiguous => "Understood",
        _ => "Add as new hold",
    };

    /// <summary>The name of the other answer.</summary>
    public static string RemoveLabel(RefreshCheck check) => check.Kind switch
    {
        UpdateExceptionKind.PossiblyRemoved or UpdateExceptionKind.ReplacedByOther => "Remove hold",
        UpdateExceptionKind.MatchedToHandPlaced => "Don't merge",
        _ => "Leave out",
    };

    /// <summary>Whether the card takes the other answer here (some kinds only take the first; a removal off the centre panel is for the full review).</summary>
    public static bool TakesRemove(RefreshCheck check) =>
        check.CanRemove && check.Kind is not (UpdateExceptionKind.LowConfidenceMatch or UpdateExceptionKind.HandPlacedAmbiguous);

    /// <summary>How an answered card was answered.</summary>
    public static string AnsweredLabel(RefreshCheck check) => check.Status == UpdateExceptionStatus.Kept ? KeepLabel(check) : RemoveLabel(check);

    /// <summary>The short name of a card's kind, beside the deck's progress.</summary>
    public static string Short(RefreshCheck check) => check.Kind switch
    {
        UpdateExceptionKind.PossiblyRemoved => "Possibly removed",
        UpdateExceptionKind.ReplacedByOther => "Other hold here",
        UpdateExceptionKind.LowConfidenceMatch => "Unsure match",
        UpdateExceptionKind.MatchedToHandPlaced => "Hand-placed, merged",
        UpdateExceptionKind.HandPlacedAmbiguous => "Hand-placed, several",
        _ => "Conflicting new",
    };
}
