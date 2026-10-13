// <copyright file="RedetectPlanner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// Decides what a re-detect of one panel photo does: which stored hold each detection is, which stored holds are the
/// same hold twice, which detections are new and which automatic holds were not found again. Pure: reads holds and
/// detections, writes nothing. Virtual holds and holds that are kept but not confirmed (hidden) are never matched,
/// changed, merged or removed; a detection on their spot is left out so it does not double them.
/// </summary>
public static class RedetectPlanner
{
    /// <summary>The decisions for one panel.</summary>
    /// <param name="input">The stored holds, the detections and what a person already ruled out.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The decisions.</returns>
    public static RedetectDecisions Plan(RedetectInput input, HoldSamenessOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        var candidates = Candidates(input, options);

        // Every matchable stored hold goes to the one detection it fits best; several holds on one detection are a group.
        var groups = candidates
            .Where(c => IsMatchable(c.Existing.Hold) && c.Verdict is HoldSamenessVerdict.Likely or HoldSamenessVerdict.Same)
            .GroupBy(c => c.Existing.Hold.Id)
            .Select(g => g.OrderByDescending(c => c.Overlap.Score).ThenBy(c => c.Detection).First())
            .GroupBy(c => c.Detection)
            .OrderBy(g => g.Key)
            .ToList();
        var draft = new RedetectDraft();
        var listed = groups.Any(g => g.Count() > 1) ? ListedForReview(input) : [];
        foreach (var group in groups)
        {
            DecideGroup([.. group], input, options, listed, draft);
        }

        var taken = candidates.Select(c => c.Detection).ToHashSet();
        var found = candidates.Select(c => c.Existing.Hold.Id).ToHashSet();
        var gone = input.Detections.Count == 0
            ? []
            : input.Existing.Where(e => !found.Contains(e.Hold.Id) && IsRemovable(e)).Select(e => e.Hold.Id).ToList();
        return new RedetectDecisions(
            draft.Shapes,
            [.. Enumerable.Range(0, input.Detections.Count).Where(i => !taken.Contains(i))],
            draft.Merges,
            draft.ForReview,
            gone,
            draft.HandShapesKept,
            taken.Count - groups.Count);
    }

    /// <summary>True for a hold a re-detect may look at: not virtual and not hidden as kept but not confirmed.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether it takes part.</returns>
    public static bool IsMatchable(Hold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return !hold.IsVirtual && hold.KeptUnconfirmedGeneration is null;
    }

    /// <summary>True when a person placed the hold or drew or edited its shape: that shape is never replaced.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether its shape is hand-made.</returns>
    public static bool IsHandShaped(Hold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return HoldMergeRules.IsHandMade(hold) || hold.ShapeControlPoints is { Count: > 0 };
    }

    /// <summary>
    /// An automatic hold that may go when it is not found again: no boulder uses it, it is not linked to another panel,
    /// and no person named it, set its colour or touched its shape.
    /// </summary>
    private static bool IsRemovable(RedetectExisting existing)
    {
        var hold = existing.Hold;
        return IsMatchable(hold)
            && hold.IsAutoDetected
            && !IsHandShaped(hold)
            && existing.Boulders.Count == 0
            && !existing.Linked
            && string.IsNullOrWhiteSpace(hold.Name)
            && (string.IsNullOrWhiteSpace(hold.Color) || hold.HasProposedColor);
    }

    /// <summary>Every stored hold and detection that are in the same place, whatever their colours.</summary>
    private static List<RedetectCandidate> Candidates(RedetectInput input, HoldSamenessOptions options)
    {
        var circles = input.Detections.Select(d => Circle(d.Shape)).ToList();
        var boxes = input.Detections.Select(d => Box(d.Shape)).ToList();
        var found = new List<RedetectCandidate>();
        foreach (var existing in input.Existing)
        {
            var box = Box(existing.Hold);
            var outlined = existing.Hold.ShapePoints is { Count: >= 3 };
            for (int i = 0; i < input.Detections.Count; i++)
            {
                if (box.MaxX < boxes[i].MinX || boxes[i].MaxX < box.MinX || box.MaxY < boxes[i].MinY || boxes[i].MaxY < box.MinY)
                {
                    continue;
                }

                // A stored hold that is only a circle is compared with the detection's circle, not its traced outline:
                // an outline fills a part of its circle only, which would read as a different size.
                var overlap = HoldSameness.Measure(existing.Hold, outlined ? input.Detections[i].Shape : circles[i]);
                if (HoldSameness.SamePlace(overlap, options))
                {
                    var color = HoldColorAgreement.Compare(existing.Hold.Color, input.Detections[i].Color);
                    found.Add(new RedetectCandidate(existing, i, overlap, HoldSameness.Judge(overlap, color, options)));
                }
            }
        }

        return found;
    }

    /// <summary>One detection and the stored holds that are it: who takes its shape, and which of them are merged.</summary>
    private static void DecideGroup(
        List<RedetectCandidate> group, RedetectInput input, HoldSamenessOptions options, HashSet<(Guid, Guid)> listed, RedetectDraft draft)
    {
        var members = group.OrderByDescending(c => c.Overlap.Score).ThenBy(c => c.Existing.Hold.Id).ToList();
        var primary = members[0];
        if (members.Count > 1)
        {
            var keeper = members.Aggregate((a, b) => HoldMergeRules.PickKeeper(
                a.Existing.Hold, a.Existing.ActiveBoulders, b.Existing.Hold, b.Existing.ActiveBoulders) == a.Existing.Hold ? a : b);
            var merged = false;
            foreach (var other in members.Where(m => m != keeper))
            {
                merged |= DecidePair(keeper.Existing, other.Existing, input, options, listed, draft);
            }

            // The detection's shape goes to the hold that survives the merges.
            primary = merged ? keeper : primary;
        }

        DecideShape(primary, input.Detections[primary.Detection].Shape, options, draft);
    }

    /// <summary>Two stored holds on one detection: merged when sure, left for the review when the review lists them.</summary>
    private static bool DecidePair(
        RedetectExisting keeper, RedetectExisting other, RedetectInput input, HoldSamenessOptions options, HashSet<(Guid, Guid)> listed, RedetectDraft draft)
    {
        var key = HoldMergeRules.PairKey(keeper.Hold.Id, other.Hold.Id);
        if (input.Dismissed.Contains(key))
        {
            return false;
        }

        var overlap = HoldSameness.Measure(keeper.Hold, other.Hold);
        var verdict = HoldSameness.Judge(overlap, HoldColorAgreement.Compare(keeper.Hold.Color, other.Hold.Color), options);

        // Two holds a person placed are there on purpose, and a boulder that uses both would end up with one hold less.
        var sure = verdict == HoldSamenessVerdict.Same
            && input.CanMerge
            && !(HoldMergeRules.IsHandMade(keeper.Hold) && HoldMergeRules.IsHandMade(other.Hold))
            && !keeper.Boulders.Overlaps(other.Boulders);
        if (sure)
        {
            draft.Merges.Add(new RedetectMerge(keeper.Hold.Id, other.Hold.Id, overlap.Score));
            return true;
        }

        if (listed.Contains(key))
        {
            draft.ForReview.Add(key);
        }

        return false;
    }

    private static void DecideShape(RedetectCandidate match, Hold fresh, HoldSamenessOptions options, RedetectDraft draft)
    {
        var hold = match.Existing.Hold;
        var outlined = hold.ShapePoints is { Count: >= 3 };
        var freshOutlined = fresh.ShapePoints is { Count: >= 3 };
        if (IsHandShaped(hold))
        {
            if (!outlined && freshOutlined)
            {
                draft.Shapes.Add(new RedetectShapeUpdate(hold.Id, match.Detection, RedetectShapeChange.FillOutline));
            }
            else
            {
                draft.HandShapesKept++;
            }

            return;
        }

        // An outline is never traded for a circle, and a shape that is as good as the same is left alone.
        if ((outlined && !freshOutlined) || SameShape(hold, fresh, outlined == freshOutlined, options))
        {
            return;
        }

        draft.Shapes.Add(new RedetectShapeUpdate(hold.Id, match.Detection, RedetectShapeChange.Replace));
    }

    private static bool SameShape(Hold hold, Hold fresh, bool sameKind, HoldSamenessOptions options)
    {
        var exact = Math.Abs(hold.X - fresh.X) <= Hold.GeometryEditTolerance
            && Math.Abs(hold.Y - fresh.Y) <= Hold.GeometryEditTolerance
            && Math.Abs(hold.Radius - fresh.Radius) <= Hold.GeometryEditTolerance
            && !hold.ShapeDiffers(fresh.ShapePoints);
        return exact || (sameKind && HoldSameness.Measure(hold, fresh).Iou >= options.UnchangedIou);
    }

    /// <summary>The pairs the duplicate review lists among the holds a re-detect looks at.</summary>
    private static HashSet<(Guid, Guid)> ListedForReview(RedetectInput input)
    {
        var holds = input.Existing.Where(e => IsMatchable(e.Hold)).ToList();
        var counts = holds.ToDictionary(e => e.Hold.Id, e => e.ActiveBoulders);
        return [.. HoldDuplicateFinder.Find([.. holds.Select(e => e.Hold)], counts).Select(c => c.Key)];
    }

    private static Hold Circle(Hold shape) => new() { X = shape.X, Y = shape.Y, Radius = shape.Radius };

    private static (double MinX, double MinY, double MaxX, double MaxY) Box(Hold hold)
    {
        var poly = HoldShapePolygon.Of(hold);
        return (
            Math.Min(poly.Min(p => p.X), hold.X - hold.Radius),
            Math.Min(poly.Min(p => p.Y), hold.Y - hold.Radius),
            Math.Max(poly.Max(p => p.X), hold.X + hold.Radius),
            Math.Max(poly.Max(p => p.Y), hold.Y + hold.Radius));
    }
}
