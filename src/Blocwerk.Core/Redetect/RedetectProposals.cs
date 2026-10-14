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

    /// <summary>...and the two are at most this far apart in size, measured across. The detected outline also has to be
    /// corroborated (<see cref="Entities.Hold.OutlineCorroborated"/>): what only one method traced is looked at.
    /// On top of that it has to pass every check of <see cref="RedetectSafety"/>.</summary>
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
    public static List<RedetectProposal> Build(RedetectInput input, RedetectDecisions decisions, IReadOnlyList<HoldStackPlan> stacks, HoldSamenessOptions options) =>
        Build(input, decisions, stacks, options, 1);

    /// <summary>Builds the proposals of one planned re-detect.</summary>
    /// <param name="input">What the planner worked on.</param>
    /// <param name="decisions">What it decided.</param>
    /// <param name="stacks">Stored holds of this photo that sit on another stored hold and are not recorded as such.</param>
    /// <param name="options">The thresholds.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>The proposals, each with the stamp of what it shows.</returns>
    public static List<RedetectProposal> Build(
        RedetectInput input, RedetectDecisions decisions, IReadOnlyList<HoldStackPlan> stacks, HoldSamenessOptions options, double aspect)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(stacks);
        var byId = input.Existing.ToDictionary(e => e.Hold.Id, e => e.Hold);
        var existing = input.Existing.ToDictionary(e => e.Hold.Id);
        var safety = new RedetectSafety(input, aspect);
        var proposals = new List<RedetectProposal>();
        proposals.AddRange(decisions.Splits.Select(s => new RedetectProposal($"split:{s.HoldId}", RedetectProposalKind.Split, s.HoldId, [], s.Detections, false, 0)
        {
            Doubt = RedetectSafety.CheckSplit(byId[s.HoldId], [.. s.Detections.Select(i => input.Detections[i].Shape)]),
        }));
        proposals.AddRange(decisions.Merges.Concat(decisions.LikelyMerges).Select(m =>
            new RedetectProposal($"merge:{m.KeptId}:{m.RemovedId}", RedetectProposalKind.Merge, m.KeptId, [m.RemovedId], [], false, m.Score)));
        proposals.AddRange(stacks.Select(s =>
            new RedetectProposal($"stack:{s.Upper.Id}:{s.Base.Id}", RedetectProposalKind.Stack, s.Upper.Id, [s.Base.Id], [], false, s.Inside)));
        proposals.AddRange(decisions.NewDetections.Select(i => new RedetectProposal($"new:{i}", RedetectProposalKind.NewHold, null, HoldsOn(input, i), [i], false, 0)
        {
            Earlier = decisions.Earlier.GetValueOrDefault(i),
            Doubt = RedetectSafety.CheckNewHold(input.Detections[i].Shape),
        }));
        proposals.AddRange(decisions.Removed.Select(id => new RedetectProposal($"gone:{id}", RedetectProposalKind.NotFound, id, [], [], false, 0)));
        proposals.AddRange(decisions.HandOffers.Select(o =>
            new RedetectProposal($"hand:{o.HoldId}", RedetectProposalKind.HandShape, o.HoldId, [], [o.Detection], false, Overlap(byId[o.HoldId], input, o.Detection).Iou)
            {
                Doubt = safety.Check(existing[o.HoldId], input.Detections[o.Detection].Shape).Doubt,
            }));

        // A circle a person placed marks the spot, not a shape: an outline for it is safe when the detector is sure
        // and the outline is about the circle's size. The hold keeps its position, name, colour and boulders.
        proposals.AddRange(decisions.Shapes.Where(s => s.Change == RedetectShapeChange.FillOutline).Select(o =>
        {
            var detection = input.Detections[o.Detection].Shape;
            var overlap = HoldSameness.Measure(byId[o.HoldId], new Entities.Hold { X = detection.X, Y = detection.Y, Radius = detection.Radius });
            var check = safety.Check(existing[o.HoldId], detection);
            var safe = check.Clean && detection.Confidence >= options.SafeDetectorConfidence && overlap.SizeRatio <= options.MaxSizeRatio
                && detection.OutlineCorroborated && detection.OutlineDistinct;
            return new RedetectProposal($"hand:{o.HoldId}", RedetectProposalKind.HandShape, o.HoldId, [], [o.Detection], safe, overlap.Iou) { Doubt = check.Doubt };
        }));
        foreach (var update in decisions.Shapes.Where(s => s.Change == RedetectShapeChange.Replace))
        {
            var overlap = Overlap(byId[update.HoldId], input, update.Detection);
            var check = safety.Check(existing[update.HoldId], input.Detections[update.Detection].Shape);
            var safe = check.Clean && overlap.Iou >= SafeIou && overlap.SizeRatio <= SafeSizeRatio && input.Detections[update.Detection].Shape.OutlineCorroborated;
            proposals.Add(new RedetectProposal($"shape:{update.HoldId}", RedetectProposalKind.BetterShape, update.HoldId, [], [update.Detection], safe, overlap.Iou)
            {
                Doubt = check.Doubt,
            });
        }

        // A proposal the detector is unsure about starts out kept: it is never also one to accept unseen.
        // A shape that covers several other holds (a whole board with its pockets, a volume) is never proposed.
        return [.. proposals.Where(p => !CoversHolds(p, input, byId))
            .Select(p => p with { Safe = p.Safe && !p.Unsure, Stamp = RedetectStamps.Of(p, byId, input.Detections) })];
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

    /// <summary>
    /// The accepted keys that may not be applied any more: the proposal is gone, or what it would write now is not what
    /// its card showed (a hold was edited in between, or another detection now stands for it).
    /// </summary>
    /// <param name="shown">The proposals the person answered.</param>
    /// <param name="current">The proposals worked out again against the holds as they are now.</param>
    /// <param name="accepted">The keys of the accepted proposals.</param>
    /// <returns>The stale keys, in order.</returns>
    public static List<string> Stale(IReadOnlyList<RedetectProposal> shown, IReadOnlyList<RedetectProposal> current, IReadOnlySet<string> accepted)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(accepted);
        var was = shown.ToDictionary(p => p.Key, p => p.Stamp);
        var now = current.ToDictionary(p => p.Key, p => p.Stamp);
        return [.. accepted.Where(k => !was.TryGetValue(k, out var stamp) || !now.TryGetValue(k, out var again) || stamp != again).Order(StringComparer.Ordinal)];
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

    /// <summary>The stored holds that lie on a new detection that is a base (<see cref="RedetectRules.OnBase"/>).</summary>
    private static List<Guid> HoldsOn(RedetectInput input, int detection)
    {
        if (RedetectRules.OnBase(input, detection).Count == 0)
        {
            return [];
        }

        var outline = Detection.Outlines.HoldShapePolygon.Of(input.Detections[detection].Shape);
        double area = Detection.Outlines.ShapeGeometry.Area(outline);
        return [.. input.Existing.Select(e => e.Hold).Where(h => RedetectRules.IsMatchable(h)
            && Detection.Outlines.ShapeGeometry.Contains(outline, new Detection.Outlines.P2(h.X, h.Y))
            && Detection.Outlines.ShapeGeometry.Area(Detection.Outlines.HoldShapePolygon.Of(h)) * RedetectRules.BaseAreaFactor <= area)
            .OrderBy(h => h.Id)
            .Select(h => h.Id)];
    }

    private static bool CoversHolds(RedetectProposal proposal, RedetectInput input, Dictionary<Guid, Entities.Hold> byId) =>
        proposal.Kind is RedetectProposalKind.BetterShape or RedetectProposalKind.HandShape
        && input.Detections[proposal.Detections[0]].Shape is { ShapePoints.Count: >= 3 } shape
        && RedetectSafety.CoveredHolds(input, Detection.Outlines.HoldShapePolygon.Of(shape), byId[proposal.HoldId!.Value]) >= RedetectSafety.MaxCoveredHolds;

    private static HoldOverlap Overlap(Entities.Hold hold, RedetectInput input, int detection) =>
        HoldSameness.Measure(hold, input.Detections[detection].Shape);
}
