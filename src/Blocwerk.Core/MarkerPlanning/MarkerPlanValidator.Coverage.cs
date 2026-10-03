// <copyright file="MarkerPlanValidator.Coverage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Coverage checks: enough markers per surface, spread out, and tying neighbours together.</summary>
public static partial class MarkerPlanValidator
{
    /// <summary>Fewer markers than this can't anchor a surface's plane.</summary>
    public const int MinMarkersPerSegment = 3;

    /// <summary>Markers spanning less than this fraction of the surface's diagonal count as bunched.</summary>
    public const double BunchedSpanFraction = 0.35;

    private static void CheckSegmentCoverage(MarkerPlan plan, Dictionary<int, PlanSegment> segments, List<PlanIssue> issues)
    {
        foreach (var segment in segments.Values)
        {
            var centres = plan.Markers
                .Where(m => m.Segment == segment.Index)
                .Select(m => new PlanVector(m.XMm, m.YMm))
                .ToList();
            if (centres.Count < MinMarkersPerSegment)
            {
                issues.Add(Error("segment-few-markers", $"\"{segment.Name}\" has {centres.Count} marker(s) — it needs at least {MinMarkersPerSegment}, spread over the surface, to measure its angle and position.", segment.Index));
                continue;
            }

            var span = centres.SelectMany(a => centres.Select(b => (a - b).Length)).Max();
            var diagonal = Math.Sqrt((segment.WidthMm * segment.WidthMm) + (segment.HeightMm * segment.HeightMm));
            if (span < BunchedSpanFraction * diagonal)
            {
                issues.Add(Warning("segment-bunched", $"The markers on \"{segment.Name}\" are bunched in one area — spread them towards its corners so the whole surface is measured, not just one patch.", segment.Index));
            }
        }
    }

    /// <summary>
    /// Every shared edge should have a marker within one photo footprint on BOTH sides, so some photo
    /// sees both surfaces at once and ties them together.
    /// </summary>
    private static void CheckSharedEdges(MarkerPlan plan, Dictionary<int, PlanSegment> segments, List<PlanIssue> issues)
    {
        var reach = MarkerSizing.FootprintHeightMm(plan.Photo);
        foreach (var child in segments.Values.Where(s => s.AttachedTo is not null))
        {
            var a = child.AttachedTo!;
            if (!segments.TryGetValue(a.ParentIndex, out var parent)
                || SegmentOutline.FindEdge(parent, a.ParentEdge) is not { } parentEdge
                || SegmentOutline.FindEdge(child, a.OwnEdge) is not { } ownEdge)
            {
                continue;
            }

            if (!HasMarkerNear(plan, parent, parentEdge, reach) || !HasMarkerNear(plan, child, ownEdge, reach))
            {
                issues.Add(Warning("shared-edge-uncovered", $"No marker near the edge where \"{child.Name}\" meets \"{parent.Name}\" on both sides — put one within {reach / 1000:0.0} m of that edge on each surface so photos can link them.", child.Index));
            }
        }
    }

    private static bool HasMarkerNear(MarkerPlan plan, PlanSegment segment, SegmentShapeEdge edge, double reach) =>
        plan.Markers.Any(m => m.Segment == segment.Index && edge.DistanceTo(new PlanVector(m.XMm, m.YMm)) <= reach);

    private static void AddTips(MarkerPlan plan, bool photoOk, List<PlanIssue> issues)
    {
        if (plan.Markers.Count == 0)
        {
            return;
        }

        issues.Add(Warning("tip-full-frame", "Tip: only markers FULLY inside a photo count — frame shots so edge markers aren't cut off, and overlap neighbouring photos by about a third."));
        issues.Add(Warning("tip-corner-photos", "Tip: make sure every corner marker shows up in at least 3 photos taken from different spots."));
        if (photoOk && plan.Markers.Any(m => m.SizeMm > MarkerSizing.FootprintHeightMm(plan.Photo) / 6))
        {
            issues.Add(Warning("tip-marker-large", "Tip: some markers are large compared to one photo — step back a little so they fit in frame with room to spare."));
        }
    }
}
