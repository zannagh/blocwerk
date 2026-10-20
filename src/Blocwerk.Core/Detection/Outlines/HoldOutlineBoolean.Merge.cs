// <copyright file="HoldOutlineBoolean.Merge.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// The merge: the union of two outlines, and when they do not touch, a bridge across the gap.
/// </summary>
/// <remarks>
/// <para><b>The bridge</b> is a morphological CLOSING restricted to the gap: the union is grown outward by a radius
/// <c>r</c> (a round-joined stroke unioned on), then shrunk back by the same <c>r</c> (the stroke of the grown outline
/// cut away). Closing never removes anything; it fills exactly the places a disc of radius <c>r</c> cannot reach, i.e.
/// the gap and the concave throat around it, with round fillets that look like one cast hold rather than two blobs and a
/// plank. <c>r</c> is half the gap plus a share of the smaller hold's size, so the neck has a width a climber would
/// recognise. Only what the closing adds INSIDE a capsule around the two nearest boundary points is kept, so the rest
/// of both outlines (their own notches and pockets) stays exactly as traced.</para>
/// <para>Should the closing still leave two pieces (degenerate shapes, precision), a straight quad between the two
/// nearest points is used instead; that always connects.</para>
/// </remarks>
internal static partial class HoldOutlineBoolean
{
    /// <summary>Share of the smaller hold's equivalent radius that the neck is wide (each side of the axis).</summary>
    private const double NeckShare = 0.45;

    /// <summary>Merges two outlines into one.</summary>
    /// <param name="a">One outline (normalized).</param>
    /// <param name="b">The other.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <param name="maxGap">The largest gap (normalized units of the photo height) that is bridged.</param>
    /// <returns>The merged outline, or null when the holds are further apart than <paramref name="maxGap"/>.</returns>
    public static HoldOutlineMerge? Merge(OutlineRings a, OutlineRings b, double aspect, double maxGap)
    {
        // A hold sitting inside the other's pocket fills that pocket: the pocket is no longer a hole of the one hold.
        var cleanA = WithoutHolesAround(a, b);
        var cleanB = WithoutHolesAround(b, a);
        var pieces = Union(cleanA, cleanB, aspect);
        if (pieces.Count == 1)
        {
            return new HoldOutlineMerge(pieces[0], 0, false);
        }

        double gap = Gap(cleanA, cleanB, aspect);
        if (gap > maxGap)
        {
            return null;
        }

        var bridged = Bridge(cleanA, cleanB, aspect, gap);
        return bridged is null ? null : new HoldOutlineMerge(bridged, gap, true);
    }

    /// <summary>The gap between two outer rings, in normalized units of the photo height (0 when they touch).</summary>
    internal static double Gap(OutlineRings a, OutlineRings b, double aspect) =>
        ShapeGeometry.Distance(Scaled(a.Outer, aspect), Scaled(b.Outer, aspect)) / Scale;

    private static OutlineRings WithoutHolesAround(OutlineRings shape, OutlineRings other) =>
        shape with { Holes = shape.Holes.Where(h => !other.Outer.Any(p => ShapeGeometry.Contains(h, p))).ToList() };

    private static OutlineRings? Bridge(OutlineRings a, OutlineRings b, double aspect, double gap)
    {
        var sa = Scaled(a.Outer, aspect);
        var sb = Scaled(b.Outer, aspect);
        var (pa, pb) = NearestPair(sa, sb);
        double neck = NeckShare * Math.Sqrt(Math.Min(ShapeGeometry.Area(sa), ShapeGeometry.Area(sb)) / Math.PI);
        double gapScaled = gap * Scale;
        float r = (float)((gapScaled / 2) + (neck * 0.75) + 1);

        using var pathA = ToPath(a, aspect);
        using var pathB = ToPath(b, aspect);
        using var union = pathA.Op(pathB, SKPathOp.Union);
        using var grown = Grow(union, r);
        using var closed = Shrink(grown, r);
        using var zone = Capsule(pa, pb, neck + (gapScaled / 2));
        using var bridge = closed?.Op(zone, SKPathOp.Intersect);
        using var joined = bridge is null ? null : union.Op(bridge, SKPathOp.Union);
        if (joined is not null && Pieces(joined, aspect) is { Count: 1 } one)
        {
            return one[0];
        }

        return QuadBridge(union, pa, pb, Math.Max(neck * 0.5, 2), aspect);
    }

    private static OutlineRings? QuadBridge(SKPath union, P2 pa, P2 pb, double halfWidth, double aspect)
    {
        var axis = pb - pa;
        double len = Math.Max(axis.Length, 1e-6);
        var n = new P2(-axis.Y / len, axis.X / len) * halfWidth;

        // Reach a little into both holds so the quad overlaps them instead of meeting them edge to edge.
        var into = axis * (Math.Max(halfWidth, 1) / len);
        using var quad = new SKPath();
        quad.MoveTo((float)(pa.X + n.X - into.X), (float)(pa.Y + n.Y - into.Y));
        quad.LineTo((float)(pb.X + n.X + into.X), (float)(pb.Y + n.Y + into.Y));
        quad.LineTo((float)(pb.X - n.X + into.X), (float)(pb.Y - n.Y + into.Y));
        quad.LineTo((float)(pa.X - n.X - into.X), (float)(pa.Y - n.Y - into.Y));
        quad.Close();
        using var joined = union.Op(quad, SKPathOp.Union);
        return joined is not null && Pieces(joined, aspect) is { Count: 1 } one ? one[0] : null;
    }

    private static SKPath? Grow(SKPath? path, float r)
    {
        if (path is null)
        {
            return null;
        }

        using var stroke = Stroke(path, r);
        return path.Op(stroke, SKPathOp.Union);
    }

    private static SKPath? Shrink(SKPath? path, float r)
    {
        if (path is null)
        {
            return null;
        }

        using var stroke = Stroke(path, r);
        return path.Op(stroke, SKPathOp.Difference);
    }

    /// <summary>The band of width 2r around a path's boundary, as a fillable path.</summary>
    private static SKPath Stroke(SKPath path, float r)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * r,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = false,
        };
        var band = new SKPath();
        paint.GetFillPath(path, band);
        return band;
    }

    private static SKPath Capsule(P2 a, P2 b, double radius)
    {
        using var path = new SKPath();
        path.MoveTo((float)a.X, (float)a.Y);
        path.LineTo((float)b.X, (float)b.Y);
        return Stroke(path, (float)radius);
    }

    private static (P2 A, P2 B) NearestPair(List<P2> a, List<P2> b)
    {
        (P2, P2) best = (a[0], b[0]);
        double bestD = double.MaxValue;
        foreach (var (from, to, flip) in new[] { (a, b, false), (b, a, true) })
        {
            foreach (var p in from)
            {
                var q = ShapeGeometry.ClosestOnBoundary(p, to);
                double d = (q - p).Length;
                if (d < bestD)
                {
                    bestD = d;
                    best = flip ? (q, p) : (p, q);
                }
            }
        }

        return best;
    }

    private static List<P2> Scaled(IReadOnlyList<P2> ring, double aspect) => ring.Select(p => ToScaled(p, aspect)).ToList();
}
