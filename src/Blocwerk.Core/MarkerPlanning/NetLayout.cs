// <copyright file="NetLayout.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Unfolds the plan into one flat drawing (the "net").
/// </summary>
/// <remarks>
/// The root sits at the origin, unrotated. Each child is hinged onto its parent's edge like a
/// cardboard net: both outlines are counter-clockwise, so on a shared edge they run in OPPOSITE
/// directions — which puts the child on the outside of the parent edge and makes the physical
/// endpoints meet without any mirroring flag. <see cref="PlanAttachment.OffsetMm"/> slides the child
/// along the parent edge: the child's endpoint on the parent-start side lands at
/// <c>start + offset · (end − start)/|end − start|</c>, where start is the parent edge's lower end
/// (its left end when horizontal) in the PARENT's own frame. Problems (no root, dangling parents,
/// cycles, edges a shape doesn't have, overlapping outlines) come back as issues, never exceptions;
/// segments that can't be reached from the root are simply left out of the net.
/// </remarks>
public static class NetLayout
{
    /// <summary>Tolerance (mm) below which touching outlines don't count as overlapping.</summary>
    public const double OverlapToleranceMm = 1.0;

    /// <summary>Lays the plan out; see the type remarks.</summary>
    public static NetLayoutResult Compute(MarkerPlan plan)
    {
        var issues = new List<PlanIssue>();
        var usable = UsableSegments(plan, issues);
        var transforms = PlaceTree(usable, issues);

        var segments = new List<NetSegment>();
        foreach (var segment in usable.Where(s => transforms.ContainsKey(s.Index)))
        {
            var t = transforms[segment.Index];
            var polygon = SegmentOutline.Vertices(segment).Select(v => t.Apply(v).ToArray()).ToList();
            segments.Add(new NetSegment(segment.Index, polygon, t.Origin.X, t.Origin.Y, t.RotationDeg));
        }

        issues.AddRange(FindOverlaps(segments, plan));
        var markers = PlaceMarkers(plan, usable, transforms);
        return new NetLayoutResult(Bounds(segments, markers), issues, transforms);
    }

    /// <summary>Segments with a sane outline and an index not used before; the rest become issues.</summary>
    private static List<PlanSegment> UsableSegments(MarkerPlan plan, List<PlanIssue> issues)
    {
        var seen = new HashSet<int>();
        var usable = new List<PlanSegment>();
        foreach (var segment in plan.Segments)
        {
            if (!seen.Add(segment.Index))
            {
                issues.Add(Error("segment-duplicate-index", $"Two segments share index {segment.Index} — give every segment its own number.", segment.Index));
                continue;
            }

            if (!(double.IsFinite(segment.WidthMm) && double.IsFinite(segment.HeightMm)
                  && segment.WidthMm > 0 && segment.HeightMm > 0))
            {
                issues.Add(Error("segment-size", $"Segment \"{segment.Name}\" needs a positive width and height.", segment.Index));
                continue;
            }

            usable.Add(segment);
        }

        return usable;
    }

    private static Dictionary<int, NetTransform> PlaceTree(List<PlanSegment> segments, List<PlanIssue> issues)
    {
        var transforms = new Dictionary<int, NetTransform>();
        var roots = segments.Where(s => s.AttachedTo is null).ToList();
        if (roots.Count == 0)
        {
            issues.Add(Error("net-no-root", "No segment is the starting point — leave exactly one segment (usually the main wall) unattached.", null));
            return transforms;
        }

        foreach (var extra in roots.Skip(1))
        {
            issues.Add(Error("net-several-roots", $"Segment \"{extra.Name}\" isn't attached to anything — attach it to the segment it touches.", extra.Index));
        }

        transforms[roots[0].Index] = new NetTransform(0, default);
        var byIndex = segments.ToDictionary(s => s.Index);
        var queue = new Queue<PlanSegment>([roots[0]]);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in segments.Where(s => s.AttachedTo?.ParentIndex == parent.Index && !transforms.ContainsKey(s.Index)))
            {
                if (TryAttach(parent, transforms[parent.Index], child, issues) is { } placed)
                {
                    transforms[child.Index] = placed;
                    queue.Enqueue(child);
                }
            }
        }

        ReportUnplaced(segments, byIndex, transforms, issues);
        return transforms;
    }

    private static NetTransform? TryAttach(PlanSegment parent, NetTransform parentT, PlanSegment child, List<PlanIssue> issues)
    {
        var a = child.AttachedTo!;
        var parentEdge = SegmentOutline.FindEdge(parent, a.ParentEdge);
        var ownEdge = SegmentOutline.FindEdge(child, a.OwnEdge);
        if (parentEdge is null || ownEdge is null)
        {
            var (who, edge) = parentEdge is null ? (parent, a.ParentEdge) : (child, a.OwnEdge);
            issues.Add(Error("attachment-edge", $"Segment \"{who.Name}\" has no {edge} edge — pick one of its {string.Join(", ", SegmentOutline.Edges(who).Select(e => e.Edge))} edges.", child.Index));
            return null;
        }

        if (!double.IsFinite(a.OffsetMm))
        {
            issues.Add(Error("attachment-offset", $"Segment \"{child.Name}\" has an invalid offset.", child.Index));
            return null;
        }

        var pe = parentEdge.Value;
        var ce = ownEdge.Value;
        var startNet = parentT.Apply(pe.Start);
        var alongNet = parentT.ApplyDirection((pe.End - pe.Start).Normalized());

        // Opposite traversal: the child's CCW direction is the reverse of the parent's.
        var parentCcwNet = parentT.ApplyDirection(pe.Direction);
        var rotation = (parentCcwNet * -1).Angle() - ce.Direction.Angle();

        // The child endpoint that meets the parent's start end: To when the parent edge starts at its
        // CCW From (the child runs backwards along it), else From.
        var anchorLocal = pe.StartsAtFrom ? ce.To : ce.From;
        var anchorNet = startNet + (alongNet * a.OffsetMm);
        var origin = anchorNet - anchorLocal.Rotate(rotation);
        return new NetTransform(rotation, origin);
    }

    private static void ReportUnplaced(
        List<PlanSegment> segments, Dictionary<int, PlanSegment> byIndex, Dictionary<int, NetTransform> placed, List<PlanIssue> issues)
    {
        foreach (var s in segments.Where(s => !placed.ContainsKey(s.Index) && s.AttachedTo is not null))
        {
            var parentIndex = s.AttachedTo!.ParentIndex;
            if (parentIndex == s.Index)
            {
                issues.Add(Error("attachment-self", $"Segment \"{s.Name}\" is attached to itself — attach it to a neighbouring segment.", s.Index));
            }
            else if (!byIndex.ContainsKey(parentIndex))
            {
                issues.Add(Error("attachment-missing-parent", $"Segment \"{s.Name}\" is attached to segment {parentIndex}, which doesn't exist.", s.Index));
            }
            else if (placed.ContainsKey(parentIndex))
            {
                // Its own attachment was refused (edge/offset) — already reported.
            }
            else
            {
                issues.Add(Error("attachment-cycle", $"Segment \"{s.Name}\" can't be reached from the starting segment (the attachments form a loop or hang off a broken one).", s.Index));
            }
        }
    }

    private static IEnumerable<PlanIssue> FindOverlaps(List<NetSegment> segments, MarkerPlan plan)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                if (ConvexOverlap.Overlaps(segments[i].PolygonMm, segments[j].PolygonMm, OverlapToleranceMm))
                {
                    var a = plan.Segments.First(s => s.Index == segments[i].Index).Name;
                    var b = plan.Segments.First(s => s.Index == segments[j].Index).Name;
                    yield return Error("net-overlap", $"In the unfolded view \"{a}\" and \"{b}\" cover each other — attach one of them to a different edge or change its offset.", segments[j].Index);
                }
            }
        }
    }

    private static List<NetMarker> PlaceMarkers(MarkerPlan plan, List<PlanSegment> segments, Dictionary<int, NetTransform> transforms)
    {
        var byIndex = segments.ToDictionary(s => s.Index);
        var result = new List<NetMarker>();
        foreach (var m in plan.Markers)
        {
            if (!byIndex.TryGetValue(m.Segment, out var segment) || !transforms.TryGetValue(m.Segment, out var t))
            {
                continue;
            }

            var corners = SegmentOutline.SquareCorners(new PlanVector(m.XMm, m.YMm), m.SizeMm / 2)
                .Select(c => t.Apply(c).ToArray())
                .ToList();
            result.Add(new NetMarker(m.Id, corners, MarkerSizing.EstimatedPx(m.SizeMm, segment, plan.Photo)));
        }

        return result;
    }

    private static NetGeometry Bounds(List<NetSegment> segments, List<NetMarker> markers)
    {
        var points = segments.SelectMany(s => s.PolygonMm).Concat(markers.SelectMany(m => m.CornersMm)).ToList();
        if (points.Count == 0)
        {
            return new NetGeometry(segments, markers, 0, 0, 0, 0);
        }

        return new NetGeometry(
            segments, markers, points.Min(p => p[0]), points.Min(p => p[1]), points.Max(p => p[0]), points.Max(p => p[1]));
    }

    private static PlanIssue Error(string code, string message, int? segment) =>
        new(PlanIssueSeverity.Error, code, message, segment, null);
}
