// <copyright file="IUpdateReviewService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The one review of a prepared wall update ("Update wall"): every hold to check before Apply in one list, with one
/// answer path. Wall admins only, never from a kiosk tablet. Every answer is written as the decision Apply reads, on the
/// run's own update session (an answer landing after another update replaced it is refused), and the summary Apply
/// promotes is made again once per call, in the background: the caller never waits for it.
/// </summary>
public interface IUpdateReviewService
{
    /// <summary>The review of a run waiting for its Apply, or null when the run is not at that step.</summary>
    Task<UpdateReview?> GetAsync(Guid refreshId);

    /// <summary>
    /// Answers items in order (Undo takes an answer back). The first answer that is refused stops the rest, which throws a
    /// <see cref="ReviewAnswersStoppedException"/>; those before it stay answered.
    /// </summary>
    /// <returns>The review as it is after the answers.</returns>
    Task<UpdateReview> AnswerAsync(Guid refreshId, IReadOnlyList<ReviewAnswerRequest> answers);

    /// <summary>Answers every unanswered item of <paramref name="group"/> (of one panel, when given) with its suggested answer.</summary>
    /// <returns>How many were answered, and the review after.</returns>
    Task<(int Answered, UpdateReview Review)> AcceptSuggestedAsync(Guid refreshId, ReviewGroup group, Guid? panelId = null);

    /// <summary>One picture of an item (JPEG), or null when there is none.</summary>
    Task<byte[]?> GetCropAsync(Guid refreshId, string key, CheckCropView view, CancellationToken ct);
}
