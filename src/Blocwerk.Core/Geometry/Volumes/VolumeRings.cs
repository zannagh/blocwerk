// <copyright file="VolumeRings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// Ring helpers for flat-sided volumes: Douglas–Peucker simplification of a closed outline, clipping a convex ring by
/// a half-plane, and the overlap (IoU) of two convex outlines (how a removed volume is recognised again).
/// </summary>
public static class VolumeRings
{
    /// <summary>
    /// A convex outline simplified to few vertices (Douglas–Peucker on the closed ring, <paramref name="toleranceMm"/>),
    /// counter-clockwise; empty when fewer than three vertices remain.
    /// </summary>
    /// <param name="ring">A closed outline (either winding).</param>
    /// <param name="toleranceMm">Largest distance a dropped vertex may have from the simplified outline, mm.</param>
    /// <returns>The simplified ring.</returns>
    public static List<(double A, double B)> Simplify(IReadOnlyList<(double A, double B)> ring, double toleranceMm)
    {
        var hull = PlanePolygon.ConvexHull(ring);
        if (hull.Count < 3)
        {
            return [];
        }

        // Split the closed ring at its two most distant vertices and simplify both chains.
        var (i0, i1) = FarthestPair(hull);
        var first = Chain(hull, i0, i1);
        var second = Chain(hull, i1, i0);
        var keep = new List<(double A, double B)>();
        keep.AddRange(DouglasPeucker(first, toleranceMm).SkipLast(1));
        keep.AddRange(DouglasPeucker(second, toleranceMm).SkipLast(1));
        var result = PlanePolygon.ConvexHull(keep);
        return result.Count >= 3 ? result : [];
    }

    /// <summary>
    /// A convex ring without cut-off corners: an edge shorter than <paramref name="minMm"/> is replaced by the point where
    /// its two neighbouring edges meet (when that is near), so a sheet's corner is one vertex again.
    /// </summary>
    /// <param name="ring">A convex counter-clockwise ring.</param>
    /// <param name="minMm">Shortest edge kept, mm.</param>
    /// <returns>The ring.</returns>
    public static List<(double A, double B)> DropShortEdges(IReadOnlyList<(double A, double B)> ring, double minMm)
    {
        var result = ring.ToList();
        while (result.Count > 3)
        {
            var n = result.Count;
            var k = Enumerable.Range(0, n).MinBy(i => Length(result[i], result[(i + 1) % n]));
            var (p, q) = (result[k], result[(k + 1) % n]);
            if (Length(p, q) >= minMm
                || LineIntersection(result[(k + n - 1) % n], p, q, result[(k + 2) % n]) is not { } corner
                || Length(corner, ((p.A + q.A) / 2, (p.B + q.B) / 2)) > 3 * minMm)
            {
                break;
            }

            result[k] = corner;
            result.RemoveAt((k + 1) % n);
        }

        return result;
    }

    /// <summary>The part of a convex ring where <c>n·p ≤ c</c> (Sutherland–Hodgman against one line).</summary>
    /// <param name="ring">A convex ring.</param>
    /// <param name="n">The half-plane's outward normal (a, b).</param>
    /// <param name="c">The offset.</param>
    /// <returns>The clipped ring (empty when nothing is left).</returns>
    public static List<(double A, double B)> ClipHalfPlane(IReadOnlyList<(double A, double B)> ring, (double A, double B) n, double c)
    {
        var output = new List<(double A, double B)>();
        for (var j = 0; j < ring.Count; j++)
        {
            var cur = ring[j];
            var prev = ring[(j + ring.Count - 1) % ring.Count];
            var dc = (n.A * cur.A) + (n.B * cur.B) - c;
            var dp = (n.A * prev.A) + (n.B * prev.B) - c;
            if ((dc <= 0) != (dp <= 0))
            {
                var t = dp / (dp - dc);
                output.Add((prev.A + (t * (cur.A - prev.A)), prev.B + (t * (cur.B - prev.B))));
            }

            if (dc <= 0)
            {
                output.Add(cur);
            }
        }

        return output;
    }

    /// <summary>Intersection over union of two convex outlines; 0 when either is degenerate.</summary>
    /// <param name="p">One outline.</param>
    /// <param name="q">The other.</param>
    /// <returns>0–1.</returns>
    public static double IoU(IReadOnlyList<(double A, double B)> p, IReadOnlyList<(double A, double B)> q)
    {
        if (p.Count < 3 || q.Count < 3)
        {
            return 0;
        }

        var inter = PlanePolygon.Area(PlanePolygon.ClipConvex(p, q));
        var union = PlanePolygon.Area(p) + PlanePolygon.Area(q) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static (int I, int J) FarthestPair(List<(double A, double B)> ring)
    {
        (int I, int J) best = (0, 1);
        var bestD = -1.0;
        for (var i = 0; i < ring.Count; i++)
        {
            for (var j = i + 1; j < ring.Count; j++)
            {
                var d = Sq(ring[i].A - ring[j].A) + Sq(ring[i].B - ring[j].B);
                if (d > bestD)
                {
                    (bestD, best) = (d, (i, j));
                }
            }
        }

        return best;
    }

    /// <summary>The vertices from <paramref name="from"/> to <paramref name="to"/> inclusive, going forward around the ring.</summary>
    private static List<(double A, double B)> Chain(List<(double A, double B)> ring, int from, int to)
    {
        var chain = new List<(double A, double B)>();
        for (var k = from; ; k = (k + 1) % ring.Count)
        {
            chain.Add(ring[k]);
            if (k == to)
            {
                return chain;
            }
        }
    }

    private static List<(double A, double B)> DouglasPeucker(List<(double A, double B)> chain, double tolerance)
    {
        if (chain.Count <= 2)
        {
            return chain;
        }

        var (a, b) = (chain[0], chain[^1]);
        var worst = 0;
        var worstD = -1.0;
        for (var k = 1; k < chain.Count - 1; k++)
        {
            var d = SegmentDistance(chain[k], a, b);
            if (d > worstD)
            {
                (worstD, worst) = (d, k);
            }
        }

        if (worstD <= tolerance)
        {
            return [a, b];
        }

        var left = DouglasPeucker(chain.GetRange(0, worst + 1), tolerance);
        var right = DouglasPeucker(chain.GetRange(worst, chain.Count - worst), tolerance);
        return [.. left.SkipLast(1), .. right];
    }

    private static double SegmentDistance((double A, double B) p, (double A, double B) a, (double A, double B) b)
    {
        double da = b.A - a.A, db = b.B - a.B;
        var len2 = (da * da) + (db * db);
        var t = len2 <= 0 ? 0 : Math.Clamp((((p.A - a.A) * da) + ((p.B - a.B) * db)) / len2, 0, 1);
        return Math.Sqrt(Sq(p.A - (a.A + (t * da))) + Sq(p.B - (a.B + (t * db))));
    }

    private static double Length((double A, double B) p, (double A, double B) q) => Math.Sqrt(Sq(p.A - q.A) + Sq(p.B - q.B));

    /// <summary>Where line p0 → p1 meets line q0 → q1; null when parallel.</summary>
    private static (double A, double B)? LineIntersection((double A, double B) p0, (double A, double B) p1, (double A, double B) q0, (double A, double B) q1)
    {
        double da = p1.A - p0.A, db = p1.B - p0.B, ea = q1.A - q0.A, eb = q1.B - q0.B;
        var den = (da * eb) - (db * ea);
        if (Math.Abs(den) < 1e-9)
        {
            return null;
        }

        var t = (((q0.A - p0.A) * eb) - ((q0.B - p0.B) * ea)) / den;
        return (p0.A + (t * da), p0.B + (t * db));
    }

    private static double Sq(double x) => x * x;
}
