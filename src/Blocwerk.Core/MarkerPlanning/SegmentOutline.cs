// <copyright file="SegmentOutline.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The outline of a <see cref="PlanSegment"/> in its own frame (origin bottom-left, y up the surface):
/// counter-clockwise vertices and named edges. A right triangle's legs are named after the bounding-box
/// side they lie on; its third edge is <see cref="SegmentEdge.Hypotenuse"/>.
/// </summary>
public static class SegmentOutline
{
    /// <summary>The outline vertices, counter-clockwise.</summary>
    public static IReadOnlyList<PlanVector> Vertices(PlanSegment segment)
    {
        var w = segment.WidthMm;
        var h = segment.HeightMm;
        PlanVector bl = new(0, 0), br = new(w, 0), tr = new(w, h), tl = new(0, h);
        if (segment.Shape == SegmentShape.Rectangle)
        {
            return [bl, br, tr, tl];
        }

        return segment.RightAngle switch
        {
            TriangleCorner.BottomLeft => [bl, br, tl],
            TriangleCorner.BottomRight => [bl, br, tr],
            TriangleCorner.TopRight => [br, tr, tl],
            _ => [bl, tr, tl],
        };
    }

    /// <summary>The outline edges, counter-clockwise, each named.</summary>
    public static IReadOnlyList<SegmentShapeEdge> Edges(PlanSegment segment)
    {
        var v = Vertices(segment);
        var edges = new List<SegmentShapeEdge>(v.Count);
        for (var i = 0; i < v.Count; i++)
        {
            var from = v[i];
            var to = v[(i + 1) % v.Count];
            edges.Add(new SegmentShapeEdge(NameOf(from, to, segment), from, to));
        }

        return edges;
    }

    /// <summary>The named edge, or null when the shape has no such edge (e.g. a triangle's missing side).</summary>
    public static SegmentShapeEdge? FindEdge(PlanSegment segment, SegmentEdge edge)
    {
        foreach (var e in Edges(segment))
        {
            if (e.Edge == edge)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>The incentre (for a rectangle: its centre).</summary>
    public static PlanVector Incentre(PlanSegment segment)
    {
        var v = Vertices(segment);
        if (v.Count != 3)
        {
            return new PlanVector(segment.WidthMm / 2, segment.HeightMm / 2);
        }

        var a = (v[1] - v[2]).Length;
        var b = (v[2] - v[0]).Length;
        var c = (v[0] - v[1]).Length;
        var sum = a + b + c;
        return new PlanVector(
            ((a * v[0].X) + (b * v[1].X) + (c * v[2].X)) / sum,
            ((a * v[0].Y) + (b * v[1].Y) + (c * v[2].Y)) / sum);
    }

    /// <summary>
    /// True when an axis-aligned square (centre, half side) lies inside the outline with at least
    /// <paramref name="marginMm"/> between each of its corners and every edge.
    /// </summary>
    public static bool ContainsSquare(PlanSegment segment, PlanVector centre, double half, double marginMm)
    {
        foreach (var edge in Edges(segment))
        {
            var inward = edge.Inward;
            foreach (var corner in SquareCorners(centre, half))
            {
                if ((corner - edge.From).Dot(inward) < marginMm - 1e-6)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>An axis-aligned square's corners in ArUco order: TL, TR, BR, BL (y up).</summary>
    public static PlanVector[] SquareCorners(PlanVector centre, double half) =>
    [
        new(centre.X - half, centre.Y + half),
        new(centre.X + half, centre.Y + half),
        new(centre.X + half, centre.Y - half),
        new(centre.X - half, centre.Y - half),
    ];

    private static SegmentEdge NameOf(PlanVector from, PlanVector to, PlanSegment segment)
    {
        const double eps = 1e-9;
        if (Math.Abs(from.Y - to.Y) < eps)
        {
            return from.Y < eps ? SegmentEdge.Bottom : SegmentEdge.Top;
        }

        if (Math.Abs(from.X - to.X) < eps)
        {
            return from.X < eps ? SegmentEdge.Left : SegmentEdge.Right;
        }

        return segment.Shape == SegmentShape.Triangle ? SegmentEdge.Hypotenuse : SegmentEdge.Top;
    }
}
