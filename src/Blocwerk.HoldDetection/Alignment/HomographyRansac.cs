namespace Blocwerk.HoldDetection.Alignment;

internal readonly record struct Correspondence(double Sx, double Sy, double Dx, double Dy);

/// <summary>
/// Robustly estimates a homography (src -> dst) from noisy correspondences using
/// RANSAC with a normalized DLT solver.
/// </summary>
internal static class HomographyRansac
{
    public static double[]? Estimate(
        IReadOnlyList<Correspondence> matches,
        out int inlierCount,
        int iterations = 2000,
        double threshold = 3.0)
    {
        inlierCount = 0;
        if (matches.Count < 4)
        {
            return null;
        }

        var rng = new Random(12345);
        var thrSq = threshold * threshold;
        int[]? bestInliers = null;
        var bestCount = 0;

        // Reused across iterations: PickFour overwrites it each pass. Hoisted out of the loop so
        // it's neither a per-iteration heap allocation nor a per-iteration stackalloc (CA2014).
        Span<int> pick = stackalloc int[4];
        for (var iter = 0; iter < iterations; iter++)
        {
            if (!PickFour(rng, matches.Count, pick))
            {
                continue;
            }

            var sample = new Correspondence[4];
            for (var i = 0; i < 4; i++)
            {
                sample[i] = matches[pick[i]];
            }

            var h = SolveNormalized(sample);
            if (h == null)
            {
                continue;
            }

            var count = 0;
            for (var i = 0; i < matches.Count; i++)
            {
                if (TransferErrorSq(h, matches[i]) < thrSq)
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestInliers = CollectInliers(h, matches, thrSq);
            }
        }

        if (bestInliers == null || bestCount < 4)
        {
            return null;
        }

        // Refit on all inliers for accuracy.
        var inlierPts = new Correspondence[bestInliers.Length];
        for (var i = 0; i < bestInliers.Length; i++)
        {
            inlierPts[i] = matches[bestInliers[i]];
        }

        var refined = SolveNormalized(inlierPts) ?? SolveNormalized(inlierPts[..4]);
        inlierCount = bestCount;
        return refined;
    }

    private static bool PickFour(Random rng, int n, Span<int> pick)
    {
        for (var i = 0; i < 4; i++)
        {
            var v = rng.Next(n);
            for (var j = 0; j < i; j++)
            {
                if (pick[j] == v)
                {
                    return false;
                }
            }

            pick[i] = v;
        }

        return true;
    }

    private static int[] CollectInliers(double[] h, IReadOnlyList<Correspondence> matches, double thrSq)
    {
        var list = new List<int>();
        for (var i = 0; i < matches.Count; i++)
        {
            if (TransferErrorSq(h, matches[i]) < thrSq)
            {
                list.Add(i);
            }
        }

        return list.ToArray();
    }

    private static double TransferErrorSq(double[] h, Correspondence c)
    {
        var w = (h[6] * c.Sx) + (h[7] * c.Sy) + h[8];
        if (Math.Abs(w) < 1e-12)
        {
            return double.MaxValue;
        }

        var x = ((h[0] * c.Sx) + (h[1] * c.Sy) + h[2]) / w;
        var y = ((h[3] * c.Sx) + (h[4] * c.Sy) + h[5]) / w;
        var dx = x - c.Dx;
        var dy = y - c.Dy;
        return (dx * dx) + (dy * dy);
    }

    private static double[]? SolveNormalized(IReadOnlyList<Correspondence> pts)
    {
        var tSrc = NormalizeMatrix(pts, src: true);
        var tDst = NormalizeMatrix(pts, src: false);
        if (tSrc == null || tDst == null)
        {
            return null;
        }

        // AtA accumulation over normalized correspondences.
        var ata = new double[9, 9];
        foreach (var c in pts)
        {
            var (sx, sy) = Apply(tSrc, c.Sx, c.Sy);
            var (dx, dy) = Apply(tDst, c.Dx, c.Dy);

            AddRow(ata, new[] { -sx, -sy, -1, 0, 0, 0, sx * dx, sy * dx, dx });
            AddRow(ata, new[] { 0, 0, 0, -sx, -sy, -1, sx * dy, sy * dy, dy });
        }

        var hNorm = LinearAlgebra.SmallestEigenvector(ata);

        // Denormalize: H = Tdst^-1 * Hnorm * Tsrc
        var tDstInv = LinearAlgebra.Mat3Inverse(tDst);
        if (tDstInv == null)
        {
            return null;
        }

        var h = LinearAlgebra.Mat3Mul(LinearAlgebra.Mat3Mul(tDstInv, hNorm), tSrc);
        if (Math.Abs(h[8]) < 1e-12)
        {
            return null;
        }

        for (var i = 0; i < 9; i++)
        {
            h[i] /= h[8];
        }

        return h;
    }

    private static void AddRow(double[,] ata, double[] r)
    {
        for (var i = 0; i < 9; i++)
        {
            for (var j = 0; j < 9; j++)
            {
                ata[i, j] += r[i] * r[j];
            }
        }
    }

    private static double[]? NormalizeMatrix(IReadOnlyList<Correspondence> pts, bool src)
    {
        double cx = 0, cy = 0;
        foreach (var c in pts)
        {
            cx += src ? c.Sx : c.Dx;
            cy += src ? c.Sy : c.Dy;
        }

        cx /= pts.Count;
        cy /= pts.Count;

        var meanDist = 0.0;
        foreach (var c in pts)
        {
            var x = (src ? c.Sx : c.Dx) - cx;
            var y = (src ? c.Sy : c.Dy) - cy;
            meanDist += Math.Sqrt((x * x) + (y * y));
        }

        meanDist /= pts.Count;
        if (meanDist < 1e-9)
        {
            return null;
        }

        var s = Math.Sqrt(2) / meanDist;
        return new[] { s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1 };
    }

    private static (double X, double Y) Apply(double[] m, double x, double y)
    {
        var w = (m[6] * x) + (m[7] * y) + m[8];
        return (((m[0] * x) + (m[1] * y) + m[2]) / w, ((m[3] * x) + (m[4] * y) + m[5]) / w);
    }
}
