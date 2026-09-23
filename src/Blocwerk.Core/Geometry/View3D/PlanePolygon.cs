// <copyright file="PlanePolygon.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>Small polygon helpers on facet-plane rings (closed, last vertex connects to the first).</summary>
public static class PlanePolygon
{
    /// <summary>Unsigned area of a ring (shoelace).</summary>
    /// <param name="ring">The ring.</param>
    /// <returns>The area.</returns>
    public static double Area(IReadOnlyList<(double A, double B)> ring) => Math.Abs(SignedArea(ring));

    /// <summary>The convex hull, counter-clockwise (Andrew's monotone chain).</summary>
    /// <param name="points">The points.</param>
    /// <returns>The hull ring.</returns>
    public static List<(double A, double B)> ConvexHull(IEnumerable<(double A, double B)> points)
    {
        var p = points.Distinct().OrderBy(q => q.A).ThenBy(q => q.B).ToList();
        if (p.Count < 3)
        {
            return p;
        }

        var hull = new List<(double A, double B)>();
        for (var pass = 0; pass < 2; pass++)
        {
            var start = hull.Count;
            foreach (var q in p)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], q) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(q);
            }

            hull.RemoveAt(hull.Count - 1);
            p.Reverse();
        }

        return hull;
    }

    /// <summary>
    /// <paramref name="subject"/> clipped to the CONVEX <paramref name="clip"/> (Sutherland–Hodgman). The
    /// subject may be concave; the result is empty when they do not overlap.
    /// </summary>
    /// <param name="subject">Any simple ring.</param>
    /// <param name="clip">A convex ring (either winding).</param>
    /// <returns>The clipped ring.</returns>
    public static List<(double A, double B)> ClipConvex(IReadOnlyList<(double A, double B)> subject, IReadOnlyList<(double A, double B)> clip)
    {
        var output = subject.ToList();
        var ccw = SignedArea(clip) >= 0 ? 1 : -1;
        for (var i = 0; i < clip.Count && output.Count > 0; i++)
        {
            var e0 = clip[i];
            var e1 = clip[(i + 1) % clip.Count];
            var input = output;
            output = [];
            for (var j = 0; j < input.Count; j++)
            {
                var cur = input[j];
                var prev = input[(j + input.Count - 1) % input.Count];
                var curIn = ccw * Cross(e0, e1, cur) >= 0;
                var prevIn = ccw * Cross(e0, e1, prev) >= 0;
                if (curIn != prevIn)
                {
                    output.Add(Intersect(prev, cur, e0, e1));
                }

                if (curIn)
                {
                    output.Add(cur);
                }
            }
        }

        return output;
    }

    /// <summary>Extent of the ring along the unit direction (dx, dy): (min, max) of the projections.</summary>
    /// <param name="ring">The ring.</param>
    /// <param name="dx">Direction a.</param>
    /// <param name="dy">Direction b.</param>
    /// <returns>Min and max.</returns>
    public static (double Min, double Max) Extent(IReadOnlyList<(double A, double B)> ring, double dx, double dy)
    {
        var s = ring.Select(p => (p.A * dx) + (p.B * dy)).ToList();
        return (s.Min(), s.Max());
    }

    /// <summary>Whether a point lies inside (or on) a CONVEX ring of either winding.</summary>
    /// <param name="convex">The convex ring.</param>
    /// <param name="p">The point.</param>
    /// <returns>True when inside.</returns>
    public static bool Contains(IReadOnlyList<(double A, double B)> convex, (double A, double B) p)
    {
        var sign = 0;
        for (var i = 0; i < convex.Count; i++)
        {
            var c = Math.Sign(Cross(convex[i], convex[(i + 1) % convex.Count], p));
            if (c != 0 && sign != 0 && c != sign)
            {
                return false;
            }

            sign = c != 0 ? c : sign;
        }

        return convex.Count >= 3;
    }

    private static double SignedArea(IReadOnlyList<(double A, double B)> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            sum += (p.A * q.B) - (q.A * p.B);
        }

        return sum / 2;
    }

    private static double Cross((double A, double B) o, (double A, double B) a, (double A, double B) b) =>
        ((a.A - o.A) * (b.B - o.B)) - ((a.B - o.B) * (b.A - o.A));

    private static (double A, double B) Intersect((double A, double B) p, (double A, double B) q, (double A, double B) e0, (double A, double B) e1)
    {
        var d1 = Cross(e0, e1, p);
        var d2 = Cross(e0, e1, q);
        var t = Math.Abs(d1 - d2) < 1e-12 ? 0 : d1 / (d1 - d2);
        return (p.A + (t * (q.A - p.A)), p.B + (t * (q.B - p.B)));
    }
}
