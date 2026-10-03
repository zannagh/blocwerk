// <copyright file="VolumeHull.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>One face of a <see cref="VolumeHull"/>.</summary>
/// <param name="Vertices">Its corners (a, b, height), counter-clockwise seen from outside.</param>
/// <param name="Edge">The base edge it stands on (edge i runs from base vertex i to i + 1), or −1 for a face between top vertices only.</param>
/// <param name="TopIndex">The highest top vertex of an edge face (−1 otherwise).</param>
public sealed record VolumeHullFace((double A, double B, double H)[] Vertices, int Edge, int TopIndex);

/// <summary>
/// The upper faces of the convex hull of a convex base polygon on the wall (height 0) and its top vertices above it
/// (their projections inside the base): an apex (a pyramid), two ridge ends (a roof), a plateau polygon (a truncated
/// pyramid, a roof with a flat ridge plank) or the high points of several peaks. Every base edge carries one face; the
/// rest lie between top vertices (a roof's gap triangles, a plateau's flat top). Points in one plane (within 0.5 mm)
/// form one face. The base face (the wall) is not returned. Brute force over all triples: a few dozen points at most.
/// </summary>
public static class VolumeHull
{
    private const double PlaneTolMm = 0.5;

    /// <summary>The faces.</summary>
    /// <param name="baseRing">The base polygon, convex, counter-clockwise.</param>
    /// <param name="top">The top vertices (a, b, height &gt; 0), inside the base.</param>
    /// <returns>The faces pointing away from the wall.</returns>
    public static List<VolumeHullFace> Build(IReadOnlyList<(double A, double B)> baseRing, IReadOnlyList<(double A, double B, double H)> top)
    {
        var n = baseRing.Count;
        var points = baseRing.Select(p => (p.A, p.B, H: 0.0)).Concat(top).ToList();
        var faces = new List<VolumeHullFace>();
        foreach (var set in FacePointSets(points))
        {
            var ring = CounterClockwise(points, set);
            if (ring.Count < 3)
            {
                continue;
            }

            var edge = Enumerable.Range(0, n).Where(i => ring.Contains(i) && ring.Contains((i + 1) % n)).DefaultIfEmpty(-1).First();
            var tops = ring.Where(k => k >= n).ToList();
            var topIndex = edge < 0 || tops.Count == 0 ? -1 : tops.MaxBy(k => points[k].H) - n;
            faces.Add(new VolumeHullFace(ring.Select(k => points[k]).ToArray(), edge, topIndex));
        }

        return faces;
    }

    /// <summary>The outward unit normal (a, b) of base edge p → q of a counter-clockwise ring.</summary>
    /// <param name="p">Edge start.</param>
    /// <param name="q">Edge end.</param>
    /// <returns>The normal.</returns>
    public static (double A, double B) Outward((double A, double B) p, (double A, double B) q)
    {
        double da = q.A - p.A, db = q.B - p.B;
        var len = Math.Sqrt((da * da) + (db * db));
        return len <= 0 ? (0, 0) : (db / len, -da / len);
    }

    /// <summary>The point sets of the upper hull's planes: every plane through three points, facing away from the wall, with no point above it.</summary>
    private static List<List<int>> FacePointSets(List<(double A, double B, double H)> points)
    {
        var sets = new List<List<int>>();
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                for (var k = j + 1; k < points.Count; k++)
                {
                    if (Plane(points[i], points[j], points[k]) is not { } plane || OnPlane(points, plane) is not { } on)
                    {
                        continue;
                    }

                    if (!sets.Any(s => on.All(s.Contains)))
                    {
                        sets.RemoveAll(s => s.All(on.Contains));
                        sets.Add(on);
                    }
                }
            }
        }

        return sets;
    }

    /// <summary>The unit plane through three points with its normal away from the wall; null when degenerate or vertical.</summary>
    private static (double Na, double Nb, double Nh, double D)? Plane((double A, double B, double H) p, (double A, double B, double H) q, (double A, double B, double H) r)
    {
        double ua = q.A - p.A, ub = q.B - p.B, uh = q.H - p.H;
        double va = r.A - p.A, vb = r.B - p.B, vh = r.H - p.H;
        double na = (ub * vh) - (uh * vb), nb = (uh * va) - (ua * vh), nh = (ua * vb) - (ub * va);
        var len = Math.Sqrt((na * na) + (nb * nb) + (nh * nh));
        if (len < 1e-9)
        {
            return null;
        }

        var sign = nh < 0 ? -1 : 1;
        (na, nb, nh) = (sign * na / len, sign * nb / len, sign * nh / len);
        return nh < 1e-6 ? null : (na, nb, nh, (na * p.A) + (nb * p.B) + (nh * p.H));
    }

    /// <summary>The points on the plane, or null when any lies above it.</summary>
    private static List<int>? OnPlane(List<(double A, double B, double H)> points, (double Na, double Nb, double Nh, double D) plane)
    {
        var on = new List<int>();
        for (var m = 0; m < points.Count; m++)
        {
            var d = (plane.Na * points[m].A) + (plane.Nb * points[m].B) + (plane.Nh * points[m].H) - plane.D;
            if (d > PlaneTolMm)
            {
                return null;
            }

            if (d >= -PlaneTolMm)
            {
                on.Add(m);
            }
        }

        return on;
    }

    /// <summary>The face's corners counter-clockwise in (a, b) (seen from outside, as the face looks away from the wall); inner and collinear points dropped.</summary>
    private static List<int> CounterClockwise(List<(double A, double B, double H)> points, List<int> set)
    {
        var sorted = set.Distinct().OrderBy(k => points[k].A).ThenBy(k => points[k].B).ToList();
        if (sorted.Count < 3)
        {
            return sorted;
        }

        double Cross(int o, int a, int b) =>
            ((points[a].A - points[o].A) * (points[b].B - points[o].B)) - ((points[a].B - points[o].B) * (points[b].A - points[o].A));

        var hull = new List<int>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var k in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], k) <= 5)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(k);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }
}
