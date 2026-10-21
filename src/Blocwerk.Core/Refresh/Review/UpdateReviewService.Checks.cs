// <copyright file="UpdateReviewService.Checks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// Every answer is checked against the review as it is now, on the run's own session: its key must be an item of the
/// review (so no write reaches a hold the review does not ask about: a card's hold, a twin, a linked neighbour hold, or
/// a new hold under a left-out key), and the answer one that item takes. The first answer that fails stops the rest.
/// </summary>
public sealed partial class UpdateReviewService
{
    /// <summary>The refusal of "accept all" for the holds not found again.</summary>
    public const string NoBulkNotFound = "Holds that were not found again are answered one by one: nobody can say for all of them at once that they are still there.";

    private async Task<(List<ReviewAnswerRequest> Valid, (string Key, string Reason)? Refused, UpdateReview Review)> CheckAgainstReviewAsync(
        BlocwerkDbContext db, WallRefresh refresh, IReadOnlyList<ReviewAnswerRequest> answers, bool onlyIfUnanswered)
    {
        var review = await ReadAsync(db, refresh) ?? throw new UserFacingException("This update can no longer be changed here.");
        var items = review.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var valid = new List<ReviewAnswerRequest>();
        foreach (var answer in answers)
        {
            if (Refusal(items.GetValueOrDefault(answer.Key), answer) is { } reason)
            {
                return (valid, (answer.Key, reason), review);
            }

            if (!onlyIfUnanswered || !items[answer.Key].IsAnswered)
            {
                valid.Add(answer);
            }
        }

        return (valid, null, review);
    }

    private static string? Refusal(ReviewItem? item, ReviewAnswerRequest answer)
    {
        if (item is null)
        {
            return $"\"{answer.Key}\" is not an item of this review (any more): read the review again.";
        }

        return answer.Answer == ReviewAnswer.Undo || item.Answers.Contains(answer.Answer)
            ? null
            : $"\"{answer.Answer}\" is not an answer to this item; it takes {string.Join(", ", item.Answers)} or undo.";
    }
}
