// <copyright file="PolygonSimplifier.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>Caps a closed ring's vertex count while keeping its silhouette (corners survive, flat runs go).</summary>
public static class PolygonSimplifier
{
    /// <summary>
    /// The ring with at most <paramref name="max"/> vertices: an even stride first thins a dense contour
    /// to twice the cap, then Visvalingam–Whyatt drops the vertex spanning the smallest triangle until
    /// the cap holds. Rings already within the cap come back unchanged.
    /// </summary>
    /// <param name="ring">The closed ring (last vertex connects to the first).</param>
    /// <param name="max">The vertex cap (≥ 3).</param>
    /// <returns>The capped ring.</returns>
    public static List<(double A, double B)> Cap(IReadOnlyList<(double A, double B)> ring, int max)
    {
        max = Math.Max(3, max);
        if (ring.Count <= max)
        {
            return [.. ring];
        }

        var points = Thin(ring, 2 * max);
        while (points.Count > max)
        {
            var smallest = 0;
            var smallestArea = double.MaxValue;
            for (var i = 0; i < points.Count; i++)
            {
                var area = TriangleArea(points[(i + points.Count - 1) % points.Count], points[i], points[(i + 1) % points.Count]);
                if (area < smallestArea)
                {
                    smallestArea = area;
                    smallest = i;
                }
            }

            points.RemoveAt(smallest);
        }

        return points;
    }

    private static List<(double A, double B)> Thin(IReadOnlyList<(double A, double B)> ring, int target)
    {
        if (ring.Count <= target)
        {
            return [.. ring];
        }

        var step = (double)ring.Count / target;
        return Enumerable.Range(0, target).Select(i => ring[(int)Math.Floor(i * step)]).ToList();
    }

    private static double TriangleArea((double A, double B) p, (double A, double B) q, (double A, double B) r) =>
        Math.Abs(((q.A - p.A) * (r.B - p.B)) - ((r.A - p.A) * (q.B - p.B))) / 2;
}
