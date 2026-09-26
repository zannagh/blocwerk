// <copyright file="PhotoCoverage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>How much of a photo's view of a facet a registration's inliers span.</summary>
/// <param name="Coverage">Inlier hull ∩ facet-in-view, over facet-in-view (0 when the facet is not in view).</param>
/// <param name="PhotoShare">Inlier hull over the whole photo.</param>
/// <param name="OverlapShare">Facet-in-view over the whole photo.</param>
internal readonly record struct PhotoCoverageResult(double Coverage, double PhotoShare, double OverlapShare);

/// <summary>
/// Measures coverage on a regular grid over the photo: a cell belongs to the facet's view when the mapping
/// lands its centre inside the facet extent (on the inliers' side of the horizon), and to the inliers when
/// it lies inside their convex hull. A grid keeps it robust where an exact polygon clip would need horizon
/// handling, and 64 × 48 cells resolve 1 % steps.
/// </summary>
internal static class PhotoCoverage
{
    private const int Cols = 64;
    private const int Rows = 48;

    /// <summary>Measures a registration's coverage.</summary>
    /// <param name="inliersPx">The inlier photo points, px.</param>
    /// <param name="width">Photo width, px.</param>
    /// <param name="height">Photo height, px.</param>
    /// <param name="toPlane">Normalised photo → plane mm.</param>
    /// <param name="depthSign">The inliers' side of the horizon.</param>
    /// <param name="extent">The facet extent.</param>
    /// <returns>The shares.</returns>
    public static PhotoCoverageResult Measure(
        IReadOnlyList<(double X, double Y)> inliersPx, int width, int height, PlaneHomography toPlane, int depthSign, PlaneRectMm extent)
    {
        var hull = ConvexHull(inliersPx.Select(p => (p.X / width, p.Y / height)).ToList());
        int overlap = 0, covered = 0, inHull = 0;
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var x = (c + 0.5) / Cols;
                var y = (r + 0.5) / Rows;
                var hulled = Inside(hull, x, y);
                inHull += hulled ? 1 : 0;
                if (!InView(toPlane, depthSign, extent, x, y))
                {
                    continue;
                }

                overlap++;
                covered += hulled ? 1 : 0;
            }
        }

        const double total = Cols * Rows;
        return new PhotoCoverageResult(overlap == 0 ? 0 : (double)covered / overlap, inHull / total, overlap / total);
    }

    /// <summary>The inliers' span on the plane along a and b (5th to 95th percentile, so a stray inlier does not count), mm.</summary>
    /// <param name="inliers">The inlier photo points, normalised.</param>
    /// <param name="toPlane">Normalised photo → plane mm.</param>
    /// <returns>The spans.</returns>
    public static (double A, double B) Spread(IReadOnlyList<(double X, double Y)> inliers, PlaneHomography toPlane)
    {
        var mapped = inliers.Select(p => toPlane.Apply(p.X, p.Y)).Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
        return mapped.Count < 2 ? (0, 0) : (Span(mapped.Select(p => p.X)), Span(mapped.Select(p => p.Y)));
    }

    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise in a y-down frame's maths orientation.</summary>
    /// <param name="points">The points.</param>
    /// <returns>The hull vertices; fewer than 3 for a degenerate set.</returns>
    internal static List<(double X, double Y)> ConvexHull(List<(double X, double Y)> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3)
        {
            return pts;
        }

        var hull = new List<(double X, double Y)>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    /// <summary>Whether (x, y) lies inside a hull from <see cref="ConvexHull"/> (false for a degenerate one).</summary>
    internal static bool Inside(List<(double X, double Y)> hull, double x, double y)
    {
        if (hull.Count < 3)
        {
            return false;
        }

        for (var i = 0; i < hull.Count; i++)
        {
            if (Cross(hull[i], hull[(i + 1) % hull.Count], (x, y)) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static double Span(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)Math.Floor(0.95 * (sorted.Count - 1))] - sorted[(int)Math.Ceiling(0.05 * (sorted.Count - 1))];
    }

    private static bool InView(PlaneHomography toPlane, int depthSign, PlaneRectMm extent, double x, double y)
    {
        if (Math.Sign(toPlane.Depth(x, y)) != depthSign)
        {
            return false;
        }

        var (a, b) = toPlane.Apply(x, y);
        return a >= extent.AMin && a <= extent.AMax && b >= extent.BMin && b <= extent.BMax;
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
        ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
}
