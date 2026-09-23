using OpenCvSharp;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>A fitted side line: a point on it, its unit direction and the inliers' squared residuals.</summary>
internal readonly record struct EdgeLine(Point2d Point, Point2d Direction, double SumSquaredResidual, int InlierCount);

/// <summary>
/// Robust line fit for the edge samples of one marker side: best 1 px consensus over point
/// pairs, then total least squares on that consensus set. The reference (refine.py) draws 200
/// random pairs; with at most 80 samples per side this tries every pair instead (≤ 3160), which is
/// deterministic and finds a consensus at least as large.
/// </summary>
internal static class EdgeLineFitter
{
    /// <summary>Consensus distance, px.</summary>
    private const double InlierThreshold = 1.0;

    /// <summary>Fewer samples than this never make a line.</summary>
    private const int MinSamples = 6;

    /// <summary>Returns null when fewer than half the samples (or fewer than 5) agree on a line.</summary>
    public static EdgeLine? Fit(IReadOnlyList<Point2d> pts)
    {
        if (pts.Count < MinSamples)
        {
            return null;
        }

        var best = BestConsensus(pts);
        if (best is null || best.Count < Math.Max(5.0, 0.5 * pts.Count))
        {
            return null;
        }

        return TotalLeastSquares(best);
    }

    /// <summary>Intersection of two lines, or null when they are (numerically) parallel.</summary>
    public static Point2d? Intersect(EdgeLine l1, EdgeLine l2)
    {
        var (p, d) = (l1.Point, l1.Direction);
        var (q, e) = (l2.Point, l2.Direction);

        // Solve p + t d = q + u e, i.e. [d, -e] (t, u)^T = q - p.
        var det = (d.X * -e.Y) - (-e.X * d.Y);
        if (Math.Abs(det) < 1e-6)
        {
            return null;
        }

        var r = q - p;
        var t = ((r.X * -e.Y) - (-e.X * r.Y)) / det;
        return p + (d * t);
    }

    private static List<Point2d>? BestConsensus(IReadOnlyList<Point2d> pts)
    {
        var xs = pts.Select(p => p.X).ToArray();
        var ys = pts.Select(p => p.Y).ToArray();
        var bestCount = 0;
        (int Origin, double Nx, double Ny) best = default;
        for (var i = 0; i < xs.Length; i++)
        {
            for (var j = i + 1; j < xs.Length; j++)
            {
                var dx = xs[j] - xs[i];
                var dy = ys[j] - ys[i];
                var len = Math.Sqrt((dx * dx) + (dy * dy));
                if (len < 1e-6)
                {
                    continue;
                }

                var count = CountInliers(xs, ys, i, -dy / len, dx / len);
                if (count > bestCount)
                {
                    bestCount = count;
                    best = (i, -dy / len, dx / len);
                }
            }
        }

        if (bestCount == 0)
        {
            return null;
        }

        var o = best.Origin;
        return pts.Where(p => IsInlier(p.X - xs[o], p.Y - ys[o], best.Nx, best.Ny)).ToList();
    }

    private static int CountInliers(double[] xs, double[] ys, int origin, double nx, double ny)
    {
        var count = 0;
        for (var k = 0; k < xs.Length; k++)
        {
            if (IsInlier(xs[k] - xs[origin], ys[k] - ys[origin], nx, ny))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsInlier(double dx, double dy, double nx, double ny) =>
        Math.Abs((dx * nx) + (dy * ny)) < InlierThreshold;

    /// <summary>Centroid + principal axis of the scatter matrix (= first right singular vector of the centred points).</summary>
    private static EdgeLine TotalLeastSquares(List<Point2d> pts)
    {
        var cx = pts.Average(p => p.X);
        var cy = pts.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in pts)
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        // Orientation of the major axis of [[sxx, sxy], [sxy, syy]].
        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var dir = new Point2d(Math.Cos(angle), Math.Sin(angle));
        var normal = new Point2d(-dir.Y, dir.X);
        var centre = new Point2d(cx, cy);
        var ss = pts.Sum(p => Math.Pow((p - centre).DotProduct(normal), 2));
        return new EdgeLine(centre, dir, ss, pts.Count);
    }
}
