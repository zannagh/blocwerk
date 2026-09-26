// <copyright file="VolumeHull.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>One face of a <see cref="VolumeHull"/>.</summary>
/// <param name="Vertices">Its corners (a, b, height), counter-clockwise seen from outside.</param>
/// <param name="Edge">The base edge it stands on (edge i runs from base vertex i to i + 1), or −1 for a face between two top vertices.</param>
/// <param name="TopIndex">The top vertex of an edge face (−1 otherwise).</param>
public sealed record VolumeHullFace((double A, double B, double H)[] Vertices, int Edge, int TopIndex);

/// <summary>
/// The upper faces of the convex hull of a convex base polygon on the wall (height 0) and one or two top vertices
/// above it (their projections inside the base). Every base edge carries one face: the plane through the edge that
/// is tangent to the hull, i.e. through the top vertex it rises to most steeply. Where two neighbouring edges rise to
/// different top vertices, the triangle between the shared base vertex and both top vertices closes the gap (a roof's
/// long sides). The base face (the wall) is not returned.
/// </summary>
public static class VolumeHull
{
    /// <summary>The faces.</summary>
    /// <param name="baseRing">The base polygon, convex, counter-clockwise.</param>
    /// <param name="top">One or two top vertices (a, b, height &gt; 0), inside the base.</param>
    /// <returns>The faces pointing away from the wall.</returns>
    public static List<VolumeHullFace> Build(IReadOnlyList<(double A, double B)> baseRing, IReadOnlyList<(double A, double B, double H)> top)
    {
        var n = baseRing.Count;
        var rises = new int[n];
        for (var i = 0; i < n; i++)
        {
            rises[i] = SteepestTop(baseRing[i], baseRing[(i + 1) % n], top);
        }

        var faces = new List<VolumeHullFace>();
        for (var i = 0; i < n; i++)
        {
            var p = baseRing[i];
            var q = baseRing[(i + 1) % n];
            var t = top[rises[i]];
            faces.Add(new VolumeHullFace(Oriented([(p.A, p.B, 0), (q.A, q.B, 0), t]), i, rises[i]));
            var next = rises[(i + 1) % n];
            if (next != rises[i])
            {
                faces.Add(new VolumeHullFace(Oriented([(q.A, q.B, 0), top[next], t]), -1, -1));
            }
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

    /// <summary>The top vertex the plane through edge p → q must pass through to keep every top vertex under it.</summary>
    private static int SteepestTop((double A, double B) p, (double A, double B) q, IReadOnlyList<(double A, double B, double H)> top)
    {
        var n = Outward(p, q);
        var best = 0;
        var bestSlope = double.NegativeInfinity;
        for (var k = 0; k < top.Count; k++)
        {
            var inward = -(((top[k].A - p.A) * n.A) + ((top[k].B - p.B) * n.B));
            var slope = top[k].H / Math.Max(inward, 1e-3);
            if (slope > bestSlope + 1e-9)
            {
                (bestSlope, best) = (slope, k);
            }
        }

        return best;
    }

    /// <summary>The triangle wound counter-clockwise seen from outside (its normal pointing away from the wall).</summary>
    private static (double A, double B, double H)[] Oriented((double A, double B, double H)[] tri)
    {
        var (p, q, r) = (tri[0], tri[1], tri[2]);
        var nh = ((q.A - p.A) * (r.B - p.B)) - ((q.B - p.B) * (r.A - p.A));
        return nh >= 0 ? tri : [p, r, q];
    }
}
