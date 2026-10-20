// <copyright file="UpdateReviewBuilder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// Works the one review out of the session's facts: one item per card, per old hold kept in place without being found
/// again, per staged detection that goes live as a new hold or was left out, and per "may have moved" suggestion. A hold
/// a card is about is asked about on its card only. Whether an item is answered comes from the same rows Apply reads
/// (a card's status, a confirmed or removed carry verdict, a detection's keep/discard), so the review and the promote
/// can never disagree. Pure.
/// </summary>
internal static partial class UpdateReviewBuilder
{
    /// <summary>The items, in the order they are asked about.</summary>
    /// <param name="facts">The session's facts.</param>
    /// <returns>The items.</returns>
    public static List<ReviewItem> Build(UpdateReviewFacts facts)
    {
        var cardOld = facts.Cards.Select(c => c.OldHoldId).OfType<Guid>().ToHashSet();
        var cardStaged = facts.Cards.Select(c => c.StagedHoldId).OfType<Guid>().ToHashSet();
        var staged = facts.Staged.ToDictionary(h => h.Id);
        var items = new List<ReviewItem>();
        items.AddRange(facts.Cards.Select(c => FromCard(facts, staged, c)));
        items.AddRange(NotFound(facts, cardOld));
        items.AddRange(Detections(facts, cardStaged));
        items.AddRange(facts.Relocations.Select(r => FromRelocation(facts, staged, r)));
        return items
            .OrderBy(i => i.Group)
            .ThenByDescending(i => i.LeavesFinding)
            .ThenByDescending(i => i.Boulders.Count)
            .ThenBy(i => i.Confidence ?? 1)
            .ThenBy(i => i.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static ReviewItem FromCard(UpdateReviewFacts facts, Dictionary<Guid, StagedFact> stagedById, WallUpdateException card)
    {
        var old = card.OldHoldId is { } id ? facts.OldHolds.GetValueOrDefault(id) : null;
        var staged = card.StagedHoldId is { } s ? stagedById.GetValueOrDefault(s) : null;
        var removable = card.Kind is not (UpdateExceptionKind.ReplacedByOther or UpdateExceptionKind.PossiblyRemoved) || old is not { OnCentre: false };

        // A removal stored before it was refused off the centre is reset at the promote: shown as what will happen.
        var answer = !removable && card.Status == UpdateExceptionStatus.Removed ? null : ReviewAnswerSets.FromCard(card.Status);
        var (x, y) = card is { X: { } cx, Y: { } cy } ? (cx, cy) : (staged?.X, staged?.Y);
        return new ReviewItem(
            $"c-{card.Id}",
            ReviewAnswerSets.GroupOf(card.Kind),
            card.PanelId ?? staged?.PanelId ?? old?.StagedPanelId,
            card.OldHoldId,
            card.StagedHoldId,
            old?.Name,
            BouldersOf(facts, card.OldHoldId),
            card.Confidence,
            null,
            ReviewAnswerSets.ForCard(card.Kind, removable),
            ReviewAnswerSets.SuggestedForCard(card.Kind),
            answer,
            answer is null ? null : Name(facts, card.DecidedByUserId),
            LeavesFinding: true,
            x,
            y);
    }

    /// <summary>
    /// Every old hold kept where it was without a twin (and no card about it): the look-over's "could not find" after
    /// Apply unless a person confirms it here. A removal (here or in the full review) stays listed, answered.
    /// </summary>
    private static IEnumerable<ReviewItem> NotFound(UpdateReviewFacts facts, HashSet<Guid> cardOld)
    {
        foreach (var (oldId, carry) in facts.Carry)
        {
            if (cardOld.Contains(oldId) || carry.PairedHoldId is not null || carry.Kind is not (CarryKind.Carried or CarryKind.Removed))
            {
                continue;
            }

            var old = facts.OldHolds.GetValueOrDefault(oldId);

            // The carry row Apply reads is the answer; the review's own row only says who gave it.
            var row = facts.Answers.GetValueOrDefault((oldId, ReviewSubject.NotFound));
            var answer = carry.Kind == CarryKind.Removed ? ReviewAnswer.Gone : carry.Confirmed ? ReviewAnswer.StillThere : (ReviewAnswer?)null;
            var spot = facts.Spots?.TryGetValue(oldId, out var at) == true ? at : null;
            yield return new ReviewItem(
                $"k-{oldId}",
                ReviewGroup.NotFound,
                old?.StagedPanelId,
                oldId,
                null,
                old?.Name,
                BouldersOf(facts, oldId),
                null,
                null,
                ReviewAnswerSets.For(ReviewGroup.NotFound),
                ReviewAnswer.StillThere,
                answer,
                answer is null ? null : Name(facts, row?.ByUserId ?? carry.ConfirmedByUserId),
                LeavesFinding: true,
                spot?.X,
                spot?.Y);
        }
    }

    private static ReviewItem FromRelocation(UpdateReviewFacts facts, Dictionary<Guid, StagedFact> stagedById, Services.RelocationSuggestion r)
    {
        var old = facts.OldHolds.GetValueOrDefault(r.OldHoldId);
        var staged = stagedById.GetValueOrDefault(r.NewHoldId);
        return new ReviewItem(
            $"r-{r.Id}",
            ReviewGroup.Moved,
            staged?.PanelId ?? old?.StagedPanelId,
            r.OldHoldId,
            r.NewHoldId,
            old?.Name,
            BouldersOf(facts, r.OldHoldId),
            r.Score,
            null,
            ReviewAnswerSets.For(ReviewGroup.Moved),
            ReviewAnswer.Dismiss,
            ReviewAnswerSets.FromRelocation(r.Status),
            null,
            LeavesFinding: false,
            staged?.X,
            staged?.Y);
    }

    private static IReadOnlyList<string> BouldersOf(UpdateReviewFacts facts, Guid? oldId) =>
        oldId is { } id ? facts.Boulders.GetValueOrDefault(id) ?? [] : [];

    private static string? Name(UpdateReviewFacts facts, Guid? userId) =>
        userId is { } id ? facts.UserNames.GetValueOrDefault(id) : null;
}
