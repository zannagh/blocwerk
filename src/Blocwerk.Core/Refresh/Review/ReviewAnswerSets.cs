// <copyright file="ReviewAnswerSets.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>Which answers each kind of review item takes, which one Apply uses unanswered, and the card mapping.</summary>
public static class ReviewAnswerSets
{
    /// <summary>The group of a card.</summary>
    public static ReviewGroup GroupOf(UpdateExceptionKind kind) => kind switch
    {
        UpdateExceptionKind.LowConfidenceMatch => ReviewGroup.Unsure,
        UpdateExceptionKind.MatchedToHandPlaced or UpdateExceptionKind.HandPlacedAmbiguous => ReviewGroup.HandPlaced,
        UpdateExceptionKind.ReplacedByOther => ReviewGroup.Replaced,
        UpdateExceptionKind.PossiblyRemoved => ReviewGroup.PossiblyRemoved,
        _ => ReviewGroup.Conflicting3D,
    };

    /// <summary>The answers a card takes, in button order.</summary>
    /// <param name="kind">The card's kind.</param>
    /// <param name="removable">Whether "remove" removes the old hold here (a hold off the centre panel cannot be removed by a card).</param>
    /// <returns>The answers.</returns>
    public static IReadOnlyList<ReviewAnswer> ForCard(UpdateExceptionKind kind, bool removable) => kind switch
    {
        UpdateExceptionKind.LowConfidenceMatch => [ReviewAnswer.Keep, ReviewAnswer.NotSame],
        UpdateExceptionKind.HandPlacedAmbiguous => [ReviewAnswer.Keep],
        UpdateExceptionKind.MatchedToHandPlaced => [ReviewAnswer.Keep, ReviewAnswer.Remove],
        UpdateExceptionKind.ReplacedByOther or UpdateExceptionKind.PossiblyRemoved =>
            removable ? [ReviewAnswer.Keep, ReviewAnswer.Remove] : [ReviewAnswer.Keep],
        _ => [ReviewAnswer.Remove, ReviewAnswer.Keep],
    };

    /// <summary>What a card's default does, as an answer: a left-out detection stays out, everything else is kept as proposed.</summary>
    public static ReviewAnswer SuggestedForCard(UpdateExceptionKind kind) =>
        kind == UpdateExceptionKind.ConflictingNew ? ReviewAnswer.Remove : ReviewAnswer.Keep;

    /// <summary>The answers of a non-card group, in button order.</summary>
    public static IReadOnlyList<ReviewAnswer> For(ReviewGroup group) => group switch
    {
        ReviewGroup.NotFound => [ReviewAnswer.StillThere, ReviewAnswer.Gone],
        ReviewGroup.NewHold => [ReviewAnswer.KeepNew, ReviewAnswer.NotAHold],
        ReviewGroup.LeftOut => [ReviewAnswer.LeaveOut, ReviewAnswer.AddAsHold],
        ReviewGroup.Moved => [ReviewAnswer.SameHold, ReviewAnswer.Moved, ReviewAnswer.Dismiss],
        _ => [],
    };

    /// <summary>What a non-card item's default does, as an answer.</summary>
    public static ReviewAnswer SuggestedFor(ReviewGroup group) => group switch
    {
        ReviewGroup.NotFound => ReviewAnswer.StillThere,
        ReviewGroup.NewHold => ReviewAnswer.KeepNew,
        ReviewGroup.LeftOut => ReviewAnswer.LeaveOut,
        _ => ReviewAnswer.Dismiss,
    };

    /// <summary>A review answer as the card answer it is.</summary>
    public static UpdateExceptionAnswer ToCard(ReviewAnswer answer) => answer switch
    {
        ReviewAnswer.Keep => UpdateExceptionAnswer.Keep,
        ReviewAnswer.Remove => UpdateExceptionAnswer.Remove,
        ReviewAnswer.NotSame => UpdateExceptionAnswer.NotSame,
        ReviewAnswer.Undo => UpdateExceptionAnswer.Undo,
        _ => throw new Services.UserFacingException("That answer does not fit this card."),
    };

    /// <summary>A card's status as the review answer it stands for (null: unanswered).</summary>
    public static ReviewAnswer? FromCard(UpdateExceptionStatus status) => status switch
    {
        UpdateExceptionStatus.Kept => ReviewAnswer.Keep,
        UpdateExceptionStatus.Removed => ReviewAnswer.Remove,
        UpdateExceptionStatus.NotSame => ReviewAnswer.NotSame,
        _ => null,
    };

    /// <summary>A review answer as the "moved?" decision it is.</summary>
    public static RelocationDecision ToRelocation(ReviewAnswer answer) => answer switch
    {
        ReviewAnswer.Moved => RelocationDecision.Moved,
        ReviewAnswer.SameHold => RelocationDecision.SameHold,
        ReviewAnswer.Dismiss or ReviewAnswer.Undo => RelocationDecision.Dismiss,
        _ => throw new Services.UserFacingException("That answer does not fit a \"may have moved\" suggestion."),
    };

    /// <summary>A suggestion's status as the review answer it stands for (null: unanswered).</summary>
    public static ReviewAnswer? FromRelocation(RelocationProposalStatus status) => status switch
    {
        RelocationProposalStatus.Accepted => ReviewAnswer.Moved,
        RelocationProposalStatus.AcceptedAsSame => ReviewAnswer.SameHold,
        RelocationProposalStatus.Dismissed => ReviewAnswer.Dismiss,
        _ => null,
    };
}
