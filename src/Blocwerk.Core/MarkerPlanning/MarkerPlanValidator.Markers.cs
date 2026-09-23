// <copyright file="MarkerPlanValidator.Markers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Per-marker checks: ids, placement inside the segment, overlaps, on-photo size.</summary>
public static partial class MarkerPlanValidator
{
    private static void CheckMarkers(
        MarkerPlan plan, Dictionary<int, PlanSegment> segments, bool photoOk, MarkerGenerationOptions options, List<PlanIssue> issues)
    {
        var seenIds = new HashSet<int>();
        foreach (var m in plan.Markers)
        {
            if (!seenIds.Add(m.Id))
            {
                issues.Add(Error("marker-duplicate-id", $"Id {m.Id} is used twice — every printed marker needs its own id.", m.Segment, m.Id));
            }

            if (m.Id is < 0 or >= ArucoDict4X4.Count)
            {
                issues.Add(Error("marker-id-range", $"Id {m.Id} doesn't exist in {ArucoDict4X4.DictionaryName} — use ids 0 to {ArucoDict4X4.Count - 1}.", m.Segment, m.Id));
            }

            if (!segments.TryGetValue(m.Segment, out var segment))
            {
                issues.Add(Error("marker-segment", $"Marker {m.Id} sits on segment {m.Segment}, which doesn't exist.", m.Segment, m.Id));
                continue;
            }

            if (!(double.IsFinite(m.SizeMm) && m.SizeMm > 0 && double.IsFinite(m.XMm) && double.IsFinite(m.YMm)))
            {
                issues.Add(Error("marker-size", $"Marker {m.Id} needs a positive size and a position.", m.Segment, m.Id));
                continue;
            }

            if (!SegmentOutline.ContainsSquare(segment, new PlanVector(m.XMm, m.YMm), m.SizeMm / 2, 0))
            {
                issues.Add(Error("marker-outside", $"Marker {m.Id} sticks out of \"{segment.Name}\" — move it inside the surface (a marker bent over an edge can't be decoded).", m.Segment, m.Id));
            }

            if (photoOk)
            {
                CheckMarkerPixels(plan.Photo, segment, m, options, issues);
            }
        }

        CheckOverlaps(plan, segments, issues);
    }

    private static void CheckMarkerPixels(PhotoSetup photo, PlanSegment segment, PlanMarker m, MarkerGenerationOptions options, List<PlanIssue> issues)
    {
        var target = m.Role == MarkerRole.Corner ? options.CornerTargetPx : options.FillerTargetPx;
        var px = MarkerSizing.EstimatedPx(m.SizeMm, segment, photo);
        if (px >= target)
        {
            return;
        }

        var needed = MarkerSizing.PickSize(target, segment, photo, options.AvailableSizesMm, out var meets);
        var fix = meets
            ? $"print it at {needed:0} mm"
            : $"print it at {Math.Ceiling(MarkerSizing.RequiredSizeMm(target, segment, photo) / 5) * 5:0} mm or photograph from closer";
        issues.Add(Warning(
            "marker-too-small",
            $"Marker {m.Id} on \"{segment.Name}\" will be about {px:0} px in a photo from {photo.DistanceMm / 1000:0.0#} m — {(m.Role == MarkerRole.Corner ? "corner" : "filler")} markers need {target:0} px. {char.ToUpperInvariant(fix[0])}{fix[1..]}.",
            m.Segment,
            m.Id));
    }

    private static void CheckOverlaps(MarkerPlan plan, Dictionary<int, PlanSegment> segments, List<PlanIssue> issues)
    {
        var valid = plan.Markers
            .Where(m => segments.ContainsKey(m.Segment) && double.IsFinite(m.SizeMm) && m.SizeMm > 0)
            .ToList();
        for (var i = 0; i < valid.Count; i++)
        {
            for (var j = i + 1; j < valid.Count; j++)
            {
                var a = valid[i];
                var b = valid[j];
                var reach = (a.SizeMm + b.SizeMm) / 2;
                if (a.Segment == b.Segment && Math.Abs(a.XMm - b.XMm) < reach && Math.Abs(a.YMm - b.YMm) < reach)
                {
                    issues.Add(Error("marker-overlap", $"Markers {a.Id} and {b.Id} overlap — move them apart (leave a white gap between them).", a.Segment, b.Id));
                }
            }
        }
    }
}
