// <copyright file="UpdateReviewBuilder.Moves.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The confirm-moves half of the "Moved?" group: one item per carried hold the measurement says may have moved. Nothing
/// moves unless a person says so: unanswered, the hold stays where it was and no boulder is marked. A move a person
/// confirmed (here or as a "changed" verdict elsewhere) is listed answered, so it can be taken back.
/// </summary>
internal static partial class UpdateReviewBuilder
{
    private static IEnumerable<ReviewItem> SuspectedMoves(UpdateReviewFacts facts, Dictionary<Guid, StagedFact> stagedById)
    {
        var suggested = facts.Relocations.Select(r => r.OldHoldId).ToHashSet();
        foreach (var line in facts.Moves ?? [])
        {
            var carry = facts.Carry.GetValueOrDefault(line.OldHoldId);
            var confirmed = carry is { Kind: CarryKind.Changed } && carry.PairedHoldId == line.NewHoldId;
            if (suggested.Contains(line.OldHoldId) || (line.Outcome != HoldMoveOutcome.Suspected && !confirmed)
                || carry?.PairedHoldId != line.NewHoldId)
            {
                continue;
            }

            var answer = confirmed ? ReviewAnswer.Moved
                : facts.Answers.GetValueOrDefault((line.OldHoldId, ReviewSubject.Move)) is { Answer: ReviewAnswer.SameHold } ? ReviewAnswer.SameHold
                : (ReviewAnswer?)null;
            var by = confirmed ? carry!.ConfirmedByUserId : facts.Answers.GetValueOrDefault((line.OldHoldId, ReviewSubject.Move))?.ByUserId;
            var staged = stagedById.GetValueOrDefault(line.NewHoldId);
            yield return new ReviewItem(
                $"m-{line.OldHoldId}",
                ReviewGroup.Moved,
                staged?.PanelId ?? facts.OldHolds.GetValueOrDefault(line.OldHoldId)?.StagedPanelId,
                line.OldHoldId,
                line.NewHoldId,
                facts.OldHolds.GetValueOrDefault(line.OldHoldId)?.Name ?? line.HoldName,
                BouldersOf(facts, line.OldHoldId),
                null,
                MoveReason(line),
                [ReviewAnswer.SameHold, ReviewAnswer.Moved],
                ReviewAnswer.SameHold,
                answer,
                answer is null ? null : Name(facts, by),
                LeavesFinding: false,
                staged?.X,
                staged?.Y);
        }
    }

    /// <summary>What was measured, for the question: "moved about 6 cm (photos)", "turned about 50° (3D model)".</summary>
    private static string MoveReason(MovedHoldLine line)
    {
        var cm = Math.Max(1, (int)Math.Round(line.DistanceMm / 10));
        var turn = line.RotationDeg is { } r && r >= 45 ? (int)Math.Round(r) : 0;
        var how = (line.DistanceMm, turn) switch
        {
            ( < 30, > 0) => $"turned about {turn}°",
            (_, > 0) => $"moved about {cm} cm and turned about {turn}°",
            _ => $"moved about {cm} cm",
        };
        return line.Source == HoldMoveSource.ThreeD ? $"{how} (3D model)" : $"{how} (photos)";
    }
}
