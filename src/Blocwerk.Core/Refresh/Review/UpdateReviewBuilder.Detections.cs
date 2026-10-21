// <copyright file="UpdateReviewBuilder.Detections.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The staged detections no old hold took and no neighbour link pairs: each goes live as a new hold, or stays out
/// because the triage suggested so (its reason is on the detection). A left-out detection is a look-over finding after
/// Apply only while it is likely a missed hold, worked out exactly as the look-over does it (confident, and covered by
/// none of the holds that go live on its photo).
/// </summary>
internal static partial class UpdateReviewBuilder
{
    private static IEnumerable<ReviewItem> Detections(UpdateReviewFacts facts, HashSet<Guid> cardStaged)
    {
        var consumed = facts.Carry.Values
            .Where(c => c.Kind != CarryKind.Removed && c.PairedHoldId is not null)
            .Select(c => c.PairedHoldId!.Value)
            .ToHashSet();
        var free = facts.Staged
            .Where(h => !h.IsVirtual && !consumed.Contains(h.Id) && !cardStaged.Contains(h.Id) && !facts.NeighbourLinked.Contains(h.Id))
            .ToList();
        var goesLive = facts.Staged.Where(h => !h.IsVirtual && !IsOut(facts, h)).ToList();
        var areas = goesLive.ToLookup(h => h.PanelId, h => Area(facts, h));
        foreach (var hold in free)
        {
            yield return hold.DiscardReason is null ? NewHold(facts, hold) : LeftOut(facts, hold, areas[hold.PanelId]);
        }
    }

    /// <summary>
    /// A new detection is answered "not a hold" while the session leaves it out (whoever did), and "it is a hold" only
    /// while it is kept and a person said so. The decision rows Apply reads are the truth; a review answer they no longer
    /// say (a step-by-step save since) is not shown. An answer survives the triage moving the detection between "new" and
    /// "left out" (a 3D re-check): it is looked up by the hold, whichever subject it was given under.
    /// </summary>
    private static ReviewItem NewHold(UpdateReviewFacts facts, StagedFact hold)
    {
        var row = AnswerAbout(facts, hold.Id);
        var answer = IsOut(facts, hold) ? ReviewAnswer.NotAHold : row?.Answer is ReviewAnswer.KeepNew or ReviewAnswer.AddAsHold ? ReviewAnswer.KeepNew : (ReviewAnswer?)null;
        return Detection(facts, hold, ReviewGroup.NewHold, "n", answer, row?.ByUserId, leavesFinding: false);
    }

    /// <summary>"Add as a hold" while the session keeps it; "not a hold" only while it is out and a person said so.</summary>
    private static ReviewItem LeftOut(UpdateReviewFacts facts, StagedFact hold, IEnumerable<HoldArea> live)
    {
        var row = AnswerAbout(facts, hold.Id);
        var answer = !IsOut(facts, hold) ? ReviewAnswer.AddAsHold : row?.Answer is ReviewAnswer.LeaveOut or ReviewAnswer.NotAHold ? ReviewAnswer.LeaveOut : (ReviewAnswer?)null;
        var overlap = DiscardedDetections.Overlap(Area(facts, hold), live);
        var likely = LookOverRules.LikelyMissed(hold.Confidence, Math.Round(overlap, 3));
        return Detection(facts, hold, ReviewGroup.LeftOut, "d", answer, row?.ByUserId, likely);
    }

    private static ReviewItem Detection(
        UpdateReviewFacts facts, StagedFact hold, ReviewGroup group, string prefix, ReviewAnswer? answer, Guid? by, bool leavesFinding) =>
        new(
            $"{prefix}-{hold.Id}",
            group,
            hold.PanelId,
            null,
            hold.Id,
            null,
            [],
            hold.Confidence,
            hold.DiscardReason,
            ReviewAnswerSets.For(group),
            ReviewAnswerSets.SuggestedFor(group),
            answer,
            answer is null ? null : Name(facts, by),
            leavesFinding,
            hold.X,
            hold.Y);

    private static Entities.WallUpdateReviewAnswer? AnswerAbout(UpdateReviewFacts facts, Guid holdId) =>
        facts.Answers.GetValueOrDefault((holdId, ReviewSubject.NewHold)) ?? facts.Answers.GetValueOrDefault((holdId, ReviewSubject.LeftOut));

    /// <summary>Whether the detection is left out as the session stands: a discarded centre detection, a removed neighbour one.</summary>
    private static bool IsOut(UpdateReviewFacts facts, StagedFact hold) =>
        hold.PanelId == facts.CentrePanelId
            ? facts.NewCentre.TryGetValue(hold.Id, out var discarded) ? discarded : hold.DiscardReason is not null
            : facts.NeighbourRemoved.Contains(hold.Id);

    private static HoldArea Area(UpdateReviewFacts facts, StagedFact hold)
    {
        (int Width, int Height)? size = facts.PhotoSizes.TryGetValue(hold.PanelId, out var known) ? known : null;
        return new HoldArea(hold.X, hold.Y, hold.Radius, hold.ShapePoints, size);
    }
}
