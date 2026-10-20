// <copyright file="HoldOutlineBoolean.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Boolean operations on hold outlines (union for the merge tool, a cut for the split tool) on SkiaSharp's
/// <see cref="SKPath.Op(SKPath, SKPathOp)"/>. Everything runs in a PIXEL-PROPORTIONAL space (x scaled by the photo's
/// aspect) so distances and offsets mean the same along both axes, and is converted back to the per-axis normalized
/// storage convention at the end.
/// </summary>
internal static partial class HoldOutlineBoolean
{
    /// <summary>Normalized units to Skia units: floats keep ~7 digits, so 1e4 leaves sub-pixel precision.</summary>
    private const float Scale = 10000f;

    /// <summary>Points per curve segment when Skia hands back quads/conics/cubics (from the bridge's round joins).</summary>
    private const int CurveSteps = 6;

    /// <summary>Douglas-Peucker tolerance on the result, in normalized units of the photo height (~0.5 px at 4000 px).</summary>
    private const double SimplifyEpsilon = 0.00012;

    /// <summary>Vertex budget of a result ring.</summary>
    private const int MaxVertices = 160;

    /// <summary>The plain union of two outlines: one piece when they overlap or touch, two when there is a gap.</summary>
    /// <param name="a">One outline.</param>
    /// <param name="b">The other.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>The pieces, largest first.</returns>
    public static List<OutlineRings> Union(OutlineRings a, OutlineRings b, double aspect)
    {
        using var pa = ToPath(a, aspect);
        using var pb = ToPath(b, aspect);
        using var union = pa.Op(pb, SKPathOp.Union);
        return union is null ? [] : Pieces(union, aspect);
    }

    /// <summary>The pieces of a path: each outer contour with the holes it encloses, largest first.</summary>
    /// <param name="path">A path in the scaled space (as built by <see cref="ToPath"/>).</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>The pieces.</returns>
    internal static List<OutlineRings> Pieces(SKPath path, double aspect)
    {
        var contours = Contours(path).Where(c => c.Count >= 3 && ShapeGeometry.Area(c) > 1).ToList();
        var depth = contours.Select(c => contours.Count(o => !ReferenceEquals(o, c) && ShapeGeometry.Contains(o, Inner(c)))).ToList();
        var pieces = new List<(List<P2> Outer, List<List<P2>> Holes)>();
        for (int i = 0; i < contours.Count; i++)
        {
            if (depth[i] % 2 == 0)
            {
                pieces.Add((contours[i], []));
            }
        }

        for (int i = 0; i < contours.Count; i++)
        {
            if (depth[i] % 2 == 1)
            {
                var probe = Inner(contours[i]);
                var owner = pieces.Where(p => ShapeGeometry.Contains(p.Outer, probe)).MinBy(p => ShapeGeometry.Area(p.Outer));
                owner.Holes?.Add(contours[i]);
            }
        }

        return pieces
            .Select(p => new OutlineRings(Normalize(p.Outer, aspect), p.Holes.Select(h => (IReadOnlyList<P2>)Normalize(h, aspect)).ToList()))
            .OrderByDescending(p => p.Area)
            .ToList();
    }

    /// <summary>Builds the even-odd path of an outline in the scaled space.</summary>
    /// <param name="rings">The outline.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>A new path; the caller disposes it.</returns>
    internal static SKPath ToPath(OutlineRings rings, double aspect)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        AddRing(path, rings.Outer, aspect);
        foreach (var hole in rings.Holes)
        {
            AddRing(path, hole, aspect);
        }

        return path;
    }

    /// <summary>A normalized point in the scaled space.</summary>
    internal static P2 ToScaled(P2 p, double aspect) => new(p.X * aspect * Scale, p.Y * Scale);

    /// <summary>A scaled-space length for a length in normalized units of the photo height.</summary>
    internal static float ToScaled(double heightUnits) => (float)(heightUnits * Scale);

    private static void AddRing(SKPath path, IReadOnlyList<P2> ring, double aspect)
    {
        if (ring.Count < 3)
        {
            return;
        }

        var first = ToScaled(ring[0], aspect);
        path.MoveTo((float)first.X, (float)first.Y);
        for (int i = 1; i < ring.Count; i++)
        {
            var p = ToScaled(ring[i], aspect);
            path.LineTo((float)p.X, (float)p.Y);
        }

        path.Close();
    }

    /// <summary>Back to normalized storage space, simplified, without repeated points.</summary>
    private static List<P2> Normalize(List<P2> scaled, double aspect)
    {
        var simplified = ShapeSimplify.Simplify(Dedupe(scaled), SimplifyEpsilon * Scale, MaxVertices);
        return simplified.Select(p => new P2(p.X / Scale / aspect, p.Y / Scale)).ToList();
    }

    private static List<P2> Dedupe(List<P2> ring)
    {
        var outList = new List<P2>(ring.Count);
        foreach (var p in ring)
        {
            if (outList.Count == 0 || (p - outList[^1]).Length > 0.05)
            {
                outList.Add(p);
            }
        }

        if (outList.Count > 1 && (outList[0] - outList[^1]).Length <= 0.05)
        {
            outList.RemoveAt(outList.Count - 1);
        }

        return outList;
    }

    /// <summary>A point just inside a contour, for the nesting test (a vertex itself sits ON the neighbour's edge too often).</summary>
    private static P2 Inner(List<P2> ring)
    {
        // The midpoint of the first edge, nudged a hair towards the ring's interior side.
        var a = ring[0];
        var b = ring[1];
        var mid = (a + b) * 0.5;
        var normal = new P2(-(b - a).Y, (b - a).X);
        double len = normal.Length;
        if (len < 1e-9)
        {
            return a;
        }

        var step = normal * (0.02 / len);
        return ShapeGeometry.Contains(ring, mid + step) ? mid + step : mid - step;
    }

    /// <summary>Flattens every contour of a path to a vertex ring (curves sampled, closing segment implied).</summary>
    private static List<List<P2>> Contours(SKPath path)
    {
        var rings = new List<List<P2>>();
        List<P2>? current = null;
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    current = [new P2(pts[0].X, pts[0].Y)];
                    rings.Add(current);
                    break;
                case SKPathVerb.Line:
                    current?.Add(new P2(pts[1].X, pts[1].Y));
                    break;
                case SKPathVerb.Quad:
                    Sample(current, pts[0], pts[1], pts[2], 1f);
                    break;
                case SKPathVerb.Conic:
                    Sample(current, pts[0], pts[1], pts[2], it.ConicWeight());
                    break;
                case SKPathVerb.Cubic:
                    SampleCubic(current, pts);
                    break;
            }
        }

        return rings;
    }

    private static void Sample(List<P2>? ring, SKPoint p0, SKPoint p1, SKPoint p2, float w)
    {
        for (int s = 1; s <= CurveSteps; s++)
        {
            double t = (double)s / CurveSteps;
            double a = (1 - t) * (1 - t);
            double b = 2 * w * t * (1 - t);
            double c = t * t;
            double d = a + b + c;
            ring?.Add(new P2(((a * p0.X) + (b * p1.X) + (c * p2.X)) / d, ((a * p0.Y) + (b * p1.Y) + (c * p2.Y)) / d));
        }
    }

    private static void SampleCubic(List<P2>? ring, SKPoint[] p)
    {
        for (int s = 1; s <= CurveSteps; s++)
        {
            double t = (double)s / CurveSteps;
            double u = 1 - t;
            double a = u * u * u;
            double b = 3 * u * u * t;
            double c = 3 * u * t * t;
            double d = t * t * t;
            ring?.Add(new P2((a * p[0].X) + (b * p[1].X) + (c * p[2].X) + (d * p[3].X), (a * p[0].Y) + (b * p[1].Y) + (c * p[2].Y) + (d * p[3].Y)));
        }
    }
}
