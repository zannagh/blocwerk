// <copyright file="RedetectProposals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// Turns what a re-detect decided into proposals, and an accepted set of proposals back into the decisions that are
/// written. Pure. The order is the order a person should look at them: splits, merges and stacks first, then new and
/// lost holds, then the offers for hand-made shapes, the plain shape updates last.
/// </summary>
public static class RedetectProposals
{
    /// <summary>A shape update is safe to accept in bulk when the old and the new outline overlap at least this much (IoU)...</summary>
    public const double SafeIou = 0.5;

    /// <summary>...and the two are at most this far apart in size, measured across.</summary>
    public const double SafeSizeRatio = 1.35;

    /// <summary>Builds the proposals of one planned re-detect.</summary>
    /// <param name="input">What the planner worked on.</param>
    /// <param name="decisions">What it decided.</param>
    /// <param name="stacks">Stored holds of this photo that sit on another stored hold and are not recorded as such.</param>
    /// <returns>The proposals.</returns>
    public static List<RedetectProposal> Build(RedetectInput input, RedetectDecisions decisions, IReadOnlyList<HoldStackPlan> stacks) =>
        Build(input, decisions, stacks, new HoldSamenessOptions());

    /// <summary>Builds the proposals of one planned re-detect.</summary>
    /// <param name="input">What the planner worked on.</param>
    /// <param name="decisions">What it decided.</param>
    /// <param name="stacks">Stored holds of this photo that sit on another stored hold and are not recorded as such.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The proposals.</returns>
    public static List<RedetectProposal> Build(RedetectInput input, RedetectDecisions decisions, IReadOnlyList<HoldStackPlan> stacks, HoldSamenessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(stacks);
        var byId = input.Existing.ToDictionary(e => e.Hold.Id, e => e.Hold);
        var proposals = new List<RedetectProposal>();
        proposals.AddRange(decisions.Splits.Select(s => new RedetectProposal($"split:{s.HoldId}", RedetectProposalKind.Split, s.HoldId, [], s.Detections, false, 0)));
        proposals.AddRange(decisions.Merges.Concat(decisions.LikelyMerges).Select(m =>
            new RedetectProposal($"merge:{m.KeptId}:{m.RemovedId}", RedetectProposalKind.Merge, m.KeptId, [m.RemovedId], [], false, m.Score)));
        proposals.AddRange(stacks.Select(s =>
            new RedetectProposal($"stack:{s.Upper.Id}:{s.Base.Id}", RedetectProposalKind.Stack, s.Upper.Id, [s.Base.Id], [], false, s.Inside)));
        proposals.AddRange(decisions.NewDetections.Select(i => new RedetectProposal($"new:{i}", RedetectProposalKind.NewHold, null, [], [i], false, 0)));
        proposals.AddRange(decisions.Removed.Select(id => new RedetectProposal($"gone:{id}", RedetectProposalKind.NotFound, id, [], [], false, 0)));
        proposals.AddRange(decisions.HandOffers.Select(o =>
            new RedetectProposal($"hand:{o.HoldId}", RedetectProposalKind.HandShape, o.HoldId, [], [o.Detection], false, Overlap(byId[o.HoldId], input, o.Detection).Iou)));

        // A circle a person placed marks the spot, not a shape: an outline for it is safe when the detector is sure
        // and the outline is about the circle's size. The hold keeps its position, name, colour and boulders.
        proposals.AddRange(decisions.Shapes.Where(s => s.Change == RedetectShapeChange.FillOutline).Select(o =>
        {
            var detection = input.Detections[o.Detection].Shape;
            var overlap = HoldSameness.Measure(byId[o.HoldId], new Entities.Hold { X = detection.X, Y = detection.Y, Radius = detection.Radius });
            var safe = detection.Confidence >= options.SafeDetectorConfidence && overlap.SizeRatio <= options.MaxSizeRatio;
            return new RedetectProposal($"hand:{o.HoldId}", RedetectProposalKind.HandShape, o.HoldId, [], [o.Detection], safe, overlap.Iou);
        }));
        foreach (var update in decisions.Shapes.Where(s => s.Change == RedetectShapeChange.Replace))
        {
            var overlap = Overlap(byId[update.HoldId], input, update.Detection);
            var safe = overlap.Iou >= SafeIou && overlap.SizeRatio <= SafeSizeRatio;
            proposals.Add(new RedetectProposal($"shape:{update.HoldId}", RedetectProposalKind.BetterShape, update.HoldId, [], [update.Detection], safe, overlap.Iou));
        }

        return proposals;
    }

    /// <summary>The decisions that are written when only the given proposals were accepted.</summary>
    /// <param name="decisions">Everything the planner decided.</param>
    /// <param name="accepted">The keys of the accepted proposals.</param>
    /// <returns>The accepted part: a split becomes a shape for its hold and new holds for the other pieces.</returns>
    public static RedetectDecisions Accepted(RedetectDecisions decisions, IReadOnlySet<string> accepted)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(accepted);
        var splits = decisions.Splits.Where(s => accepted.Contains($"split:{s.HoldId}")).ToList();
        var shapes = decisions.Shapes
            .Where(s => accepted.Contains(s.Change == RedetectShapeChange.FillOutline ? $"hand:{s.HoldId}" : $"shape:{s.HoldId}"))
            .Concat(decisions.HandOffers.Where(o => accepted.Contains($"hand:{o.HoldId}")))
            .Concat(splits.Select(s => new RedetectShapeUpdate(s.HoldId, s.Detections[0], RedetectShapeChange.Replace)));
        var added = decisions.NewDetections.Where(i => accepted.Contains($"new:{i}")).Concat(splits.SelectMany(s => s.Detections.Skip(1)));
        return new RedetectDecisions(
            [.. shapes],
            [.. added.Distinct().Order()],
            [.. decisions.Merges.Concat(decisions.LikelyMerges).Where(m => accepted.Contains($"merge:{m.KeptId}:{m.RemovedId}"))],
            decisions.ForReview,
            [.. decisions.Removed.Where(id => accepted.Contains($"gone:{id}"))],
            decisions.HandShapesKept,
            decisions.SkippedDetections);
    }

    /// <summary>The upper holds of the accepted stack proposals.</summary>
    /// <param name="accepted">The keys of the accepted proposals.</param>
    /// <returns>Upper hold id and base hold id per accepted stack.</returns>
    public static List<(Guid Upper, Guid Base)> AcceptedStacks(IReadOnlySet<string> accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var stacks = new List<(Guid, Guid)>();
        foreach (var parts in accepted.Select(k => k.Split(':')).Where(p => p.Length == 3 && p[0] == "stack"))
        {
            if (Guid.TryParse(parts[1], out var upper) && Guid.TryParse(parts[2], out var lower))
            {
                stacks.Add((upper, lower));
            }
        }

        return stacks;
    }

    private static HoldOverlap Overlap(Entities.Hold hold, RedetectInput input, int detection) =>
        HoldSameness.Measure(hold, input.Detections[detection].Shape);
}
