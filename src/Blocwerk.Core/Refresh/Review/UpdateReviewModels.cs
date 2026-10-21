// <copyright file="UpdateReviewModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The one review of a prepared wall update: every hold whose outcome a person may want to check before Apply, from
/// the confirm screen's cards, the old holds not found again, the new detections, the detections the triage left out
/// and the "this hold may have moved" suggestions. One list, one set of counts.
/// </summary>
/// <param name="RefreshId">The run.</param>
/// <param name="SessionId">The run's update session.</param>
/// <param name="DecisionsVersion">The version of the summary Apply would promote (null while none is made).</param>
/// <param name="SummaryUpdating">The summary is being made again after an answer: Apply waits for it.</param>
/// <param name="Panels">The re-photographed panels, centre first.</param>
/// <param name="Items">Everything to check, in the order it is asked about.</param>
/// <param name="Counts">The counts of <paramref name="Items"/>.</param>
public sealed record UpdateReview(
    Guid RefreshId,
    Guid? SessionId,
    string? DecisionsVersion,
    bool SummaryUpdating,
    IReadOnlyList<ReviewPanel> Panels,
    IReadOnlyList<ReviewItem> Items,
    ReviewCounts Counts);

/// <summary>A re-photographed panel of the review.</summary>
/// <param name="PanelId">The staged panel.</param>
/// <param name="Col">Grid column.</param>
/// <param name="Row">Grid row.</param>
/// <param name="Name">Its name for people ("Centre panel").</param>
public sealed record ReviewPanel(Guid PanelId, int Col, int Row, string Name);

/// <summary>One thing to check.</summary>
/// <param name="Key">Stable key: <c>c-{card}</c>, <c>k-{old hold}</c>, <c>n-{detection}</c>, <c>d-{detection}</c>, <c>r-{suggestion}</c>.</param>
/// <param name="Group">What kind of question it is.</param>
/// <param name="PanelId">The staged panel it is on (null for a legacy hold without a panel).</param>
/// <param name="OldHoldId">The old live hold it is about, when any.</param>
/// <param name="StagedHoldId">The detection it is about, when any.</param>
/// <param name="HoldName">The old hold's name or colour, when it has one.</param>
/// <param name="Boulders">The boulders that use the old hold.</param>
/// <param name="Confidence">The matcher's or the detector's confidence, when known.</param>
/// <param name="Reason">Why a detection was left out (the triage's reason), when it was.</param>
/// <param name="Answers">The answers it takes, in button order.</param>
/// <param name="Suggested">What Apply does when nobody answers.</param>
/// <param name="Answer">A person's answer, or null.</param>
/// <param name="AnsweredBy">Who answered, when known.</param>
/// <param name="LeavesFinding">An unanswered item becomes a look-over finding after Apply.</param>
/// <param name="X">The spot across the new photo (normalised), when known.</param>
/// <param name="Y">The spot down the new photo (normalised), when known.</param>
public sealed record ReviewItem(
    string Key,
    ReviewGroup Group,
    Guid? PanelId,
    Guid? OldHoldId,
    Guid? StagedHoldId,
    string? HoldName,
    IReadOnlyList<string> Boulders,
    double? Confidence,
    string? Reason,
    IReadOnlyList<ReviewAnswer> Answers,
    ReviewAnswer Suggested,
    ReviewAnswer? Answer,
    string? AnsweredBy,
    bool LeavesFinding,
    double? X = null,
    double? Y = null)
{
    /// <summary>Gets a value indicating whether a person answered it.</summary>
    public bool IsAnswered => Answer is not null;

    /// <summary>
    /// Gets a value indicating whether it stays open for the look-over when Apply runs now: unanswered, or answered "not the
    /// same hold" (the old hold is then carried blind where the matcher predicts it and the look-over asks where it is).
    /// </summary>
    public bool IsOpen => LeavesFinding && Answer is null or ReviewAnswer.NotSame;
}

/// <summary>The counts of a review, the one definition every screen and the API use.</summary>
/// <param name="Open">The items that become look-over findings after Apply (unanswered, or answered "not the same hold").</param>
/// <param name="ToCheck">Unanswered items.</param>
/// <param name="Answered">Items a person answered.</param>
/// <param name="Total">All items.</param>
/// <param name="Groups">The same, per group (only groups with items).</param>
public sealed record ReviewCounts(int Open, int ToCheck, int Answered, int Total, IReadOnlyList<ReviewGroupCount> Groups)
{
    /// <summary>The counts of <paramref name="items"/>.</summary>
    public static ReviewCounts Of(IReadOnlyCollection<ReviewItem> items) =>
        new(
            items.Count(i => i.IsOpen),
            items.Count(i => !i.IsAnswered),
            items.Count(i => i.IsAnswered),
            items.Count,
            items.GroupBy(i => i.Group)
                .OrderBy(g => g.Key)
                .Select(g => new ReviewGroupCount(g.Key, g.Count(i => i.IsOpen), g.Count(i => !i.IsAnswered), g.Count(i => i.IsAnswered), g.Count()))
                .ToList());
}

/// <summary>The counts of one group.</summary>
public sealed record ReviewGroupCount(ReviewGroup Group, int Open, int ToCheck, int Answered, int Total);

/// <summary>One answer sent to the review.</summary>
/// <param name="Key">The item's key.</param>
/// <param name="Answer">The answer.</param>
public sealed record ReviewAnswerRequest(string Key, ReviewAnswer Answer);
