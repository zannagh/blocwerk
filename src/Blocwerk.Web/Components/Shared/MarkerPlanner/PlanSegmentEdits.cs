// <copyright file="PlanSegmentEdits.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>
/// The planner's segment edits, as pure functions over the immutable <see cref="MarkerPlan"/>: every
/// edit returns a new plan, so the page can validate and redraw after each one.
/// </summary>
public static class PlanSegmentEdits
{
    /// <summary>Depth of a newly attached segment, away from its parent edge.</summary>
    public const double NewSegmentDepthMm = 1000;

    private static readonly SegmentEdge[] PreferredParentEdges =
        [SegmentEdge.Right, SegmentEdge.Top, SegmentEdge.Left, SegmentEdge.Bottom, SegmentEdge.Hypotenuse];

    /// <summary>The empty-state plan: one 4 × 3 m vertical rectangle, photographed from 2.5 m on a phone.</summary>
    public static MarkerPlan NewPlan() => new(
        MarkerPlan.CurrentSchemaVersion,
        ArucoDict4X4.DictionaryName,
        MarkerCameraPresets.Create(MarkerCameraPresets.Phone1X, 2500),
        [new PlanSegment(0, "main wall", SegmentShape.Rectangle, 4000, 3000, TriangleCorner.BottomLeft, 0, 0, null)],
        []);

    /// <summary>The edges <paramref name="segment"/>'s shape actually has.</summary>
    public static IReadOnlyList<SegmentEdge> EdgesOf(PlanSegment segment) =>
        SegmentOutline.Edges(segment).Select(e => e.Edge).ToList();

    /// <summary>
    /// Attaches a new rectangle or right triangle to <paramref name="parentIndex"/> on its first free
    /// edge (right, top, left, bottom, hypotenuse), as long as that edge and 1 m deep, tilted like its
    /// parent. The owner then corrects sizes and angles in the form.
    /// </summary>
    public static MarkerPlan AddSegment(MarkerPlan plan, int parentIndex, SegmentShape shape, out int newIndex)
    {
        var parent = plan.Segments.First(s => s.Index == parentIndex);
        newIndex = plan.Segments.Count == 0 ? 0 : plan.Segments.Max(s => s.Index) + 1;
        var parentEdge = FreeEdge(plan, parent);
        var length = Math.Round(SegmentOutline.FindEdge(parent, parentEdge)?.Length ?? NewSegmentDepthMm);
        var segment = shape == SegmentShape.Triangle
            ? NewTriangle(newIndex, parentEdge, length)
            : NewRectangle(newIndex, parentEdge, length);
        segment = segment with
        {
            OverhangDeg = parent.OverhangDeg,
            YawDeg = parent.YawDeg,
            AttachedTo = segment.AttachedTo! with { ParentIndex = parentIndex },
        };
        return plan with { Segments = [.. plan.Segments, segment] };
    }

    /// <summary>Replaces the segment with the same index.</summary>
    public static MarkerPlan UpdateSegment(MarkerPlan plan, PlanSegment updated) =>
        plan with { Segments = plan.Segments.Select(s => s.Index == updated.Index ? updated : s).ToList() };

    /// <summary>
    /// Removes a segment and the markers on it. Refused (null, with the reason) for the root, which
    /// everything hangs off, and for a segment others are attached to — delete those first.
    /// </summary>
    public static MarkerPlan? DeleteSegment(MarkerPlan plan, int index, out string? refusal)
    {
        var segment = plan.Segments.FirstOrDefault(s => s.Index == index);
        refusal = null;
        if (segment is null)
        {
            return plan;
        }

        if (segment.AttachedTo is null)
        {
            refusal = "The root surface can't be deleted — every other surface is attached to it.";
            return null;
        }

        var children = plan.Segments.Where(s => s.AttachedTo?.ParentIndex == index).Select(s => s.Name).ToList();
        if (children.Count > 0)
        {
            refusal = $"Delete or re-attach what hangs off this surface first: {string.Join(", ", children)}.";
            return null;
        }

        return plan with
        {
            Segments = plan.Segments.Where(s => s.Index != index).ToList(),
            Markers = plan.Markers.Where(m => m.Segment != index).ToList(),
        };
    }

    private static SegmentEdge FreeEdge(MarkerPlan plan, PlanSegment parent)
    {
        var own = EdgesOf(parent);
        var used = plan.Segments
            .Where(s => s.AttachedTo?.ParentIndex == parent.Index)
            .Select(s => s.AttachedTo!.ParentEdge)
            .ToHashSet();
        if (parent.AttachedTo is { } attachment)
        {
            used.Add(attachment.OwnEdge);
        }

        var candidates = PreferredParentEdges.Where(own.Contains).ToList();
        return candidates.FirstOrDefault(e => !used.Contains(e), candidates.FirstOrDefault());
    }

    private static PlanSegment NewRectangle(int index, SegmentEdge parentEdge, double length)
    {
        var (ownEdge, sideways) = parentEdge switch
        {
            SegmentEdge.Right => (SegmentEdge.Left, true),
            SegmentEdge.Left => (SegmentEdge.Right, true),
            SegmentEdge.Top => (SegmentEdge.Bottom, false),
            _ => (parentEdge == SegmentEdge.Bottom ? SegmentEdge.Top : SegmentEdge.Bottom, false),
        };
        var (w, h) = sideways ? (NewSegmentDepthMm, length) : (length, NewSegmentDepthMm);
        return new PlanSegment(
            index, $"surface {index}", SegmentShape.Rectangle, w, h, TriangleCorner.BottomLeft, 0, 0,
            new PlanAttachment(0, parentEdge, ownEdge, 0));
    }

    private static PlanSegment NewTriangle(int index, SegmentEdge parentEdge, double length)
    {
        var (ownEdge, corner, w, h) = parentEdge switch
        {
            SegmentEdge.Right => (SegmentEdge.Left, TriangleCorner.BottomLeft, NewSegmentDepthMm, length),
            SegmentEdge.Left => (SegmentEdge.Right, TriangleCorner.BottomRight, NewSegmentDepthMm, length),
            SegmentEdge.Top => (SegmentEdge.Bottom, TriangleCorner.BottomLeft, length, NewSegmentDepthMm),
            SegmentEdge.Bottom => (SegmentEdge.Top, TriangleCorner.TopLeft, length, NewSegmentDepthMm),
            _ => (SegmentEdge.Hypotenuse, TriangleCorner.BottomLeft, Math.Round(length / Math.Sqrt(2)), Math.Round(length / Math.Sqrt(2))),
        };
        return new PlanSegment(
            index, $"triangle {index}", SegmentShape.Triangle, w, h, corner, 0, 0,
            new PlanAttachment(0, parentEdge, ownEdge, 0));
    }
}
