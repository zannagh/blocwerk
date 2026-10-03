namespace Blocwerk.Core.Geometry;

/// <summary>One point pair for a homography fit: source (x, y) maps to destination (x, y).</summary>
public readonly record struct PointCorrespondence(double SrcX, double SrcY, double DstX, double DstY);

/// <summary>
/// A 3x3 planar homography (row-major, h33 = 1) with a pure-C# least-squares DLT fit, so the
/// geometry layer needs no native OpenCV. Kept separate from the internal
/// <c>Blocwerk.HoldDetection.Alignment.HomographyRansac</c> (feature alignment), which lives in a
/// project Core cannot reference.
/// </summary>
public sealed class PlaneHomography
{
    private readonly double[] h;

    private PlaneHomography(double[] coefficients)
    {
        h = coefficients;
    }

    /// <summary>A copy of the 9 row-major coefficients.</summary>
    public double[] Coefficients => (double[])h.Clone();

    /// <summary>Wraps 9 row-major coefficients (normalized so that h33 = 1 when possible).</summary>
    public static PlaneHomography FromCoefficients(IReadOnlyList<double> coefficients)
    {
        if (coefficients.Count != 9)
        {
            throw new ArgumentException("A homography has 9 coefficients.", nameof(coefficients));
        }

        var m = coefficients.ToArray();
        if (Math.Abs(m[8]) > 1e-15)
        {
            var s = m[8];
            for (var i = 0; i < 9; i++)
            {
                m[i] /= s;
            }
        }

        return new PlaneHomography(m);
    }

    /// <summary>
    /// Least-squares fit (Hartley-normalized DLT). Exact for 4 points in general position.
    /// Returns null for fewer than 4 points or a degenerate configuration.
    /// </summary>
    public static PlaneHomography? Fit(IReadOnlyList<PointCorrespondence> pairs)
    {
        if (pairs.Count < 4)
        {
            return null;
        }

        var tSrc = Normalizer(pairs.Select(p => (p.SrcX, p.SrcY)));
        var tDst = Normalizer(pairs.Select(p => (p.DstX, p.DstY)));
        if (tSrc is null || tDst is null)
        {
            return null;
        }

        var ata = new double[8, 8];
        var atb = new double[8];
        foreach (var p in pairs)
        {
            var (x, y) = Transform(tSrc, p.SrcX, p.SrcY);
            var (u, v) = Transform(tDst, p.DstX, p.DstY);
            Accumulate(ata, atb, [x, y, 1, 0, 0, 0, -x * u, -y * u], u);
            Accumulate(ata, atb, [0, 0, 0, x, y, 1, -x * v, -y * v], v);
        }

        var sol = SolveLinear(ata, atb);
        if (sol is null)
        {
            return null;
        }

        double[] hn = [sol[0], sol[1], sol[2], sol[3], sol[4], sol[5], sol[6], sol[7], 1.0];
        var dstInv = Invert(tDst);
        return dstInv is null ? null : FromCoefficients(Multiply(Multiply(dstInv, hn), tSrc));
    }

    /// <summary>Maps a point; returns NaNs when it lands on the line at infinity.</summary>
    public (double X, double Y) Apply(double x, double y)
    {
        var w = (h[6] * x) + (h[7] * y) + h[8];
        if (Math.Abs(w) < 1e-12)
        {
            return (double.NaN, double.NaN);
        }

        return (((h[0] * x) + (h[1] * y) + h[2]) / w, ((h[3] * x) + (h[4] * y) + h[5]) / w);
    }

    /// <summary>The projective depth <c>w</c> at a point; its sign flips across the horizon.</summary>
    public double Depth(double x, double y) => (h[6] * x) + (h[7] * y) + h[8];

    /// <summary>The inverse mapping, or null when singular.</summary>
    public PlaneHomography? Inverse()
    {
        var inv = Invert(h);
        return inv is null ? null : FromCoefficients(inv);
    }

    /// <summary>The composition "this, then <paramref name="next"/>": maps x to next(this(x)).</summary>
    /// <param name="next">The mapping applied second.</param>
    /// <returns>The composed homography.</returns>
    public PlaneHomography Then(PlaneHomography next) => FromCoefficients(Multiply(next.h, h));

    /// <summary>The 2x2 Jacobian d(dst)/d(src) at a source point, row-major (dX/dx, dX/dy, dY/dx, dY/dy).</summary>
    public (double Dxx, double Dxy, double Dyx, double Dyy) Jacobian(double x, double y)
    {
        var w = Depth(x, y);
        var (u, v) = Apply(x, y);
        return (
            (h[0] - (u * h[6])) / w,
            (h[1] - (u * h[7])) / w,
            (h[3] - (v * h[6])) / w,
            (h[4] - (v * h[7])) / w);
    }

    private static void Accumulate(double[,] ata, double[] atb, double[] row, double rhs)
    {
        for (var i = 0; i < 8; i++)
        {
            atb[i] += row[i] * rhs;
            for (var j = 0; j < 8; j++)
            {
                ata[i, j] += row[i] * row[j];
            }
        }
    }

    /// <summary>Gaussian elimination with partial pivoting; null when (near) singular.</summary>
    private static double[]? SolveLinear(double[,] a, double[] b)
    {
        var n = b.Length;
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(a[pivot, col]) < 1e-12)
            {
                return null;
            }

            SwapRows(a, b, col, pivot);
            for (var r = col + 1; r < n; r++)
            {
                var f = a[r, col] / a[col, col];
                for (var c = col; c < n; c++)
                {
                    a[r, c] -= f * a[col, c];
                }

                b[r] -= f * b[col];
            }
        }

        var x = new double[n];
        for (var r = n - 1; r >= 0; r--)
        {
            var s = b[r];
            for (var c = r + 1; c < n; c++)
            {
                s -= a[r, c] * x[c];
            }

            x[r] = s / a[r, r];
        }

        return x;
    }

    private static void SwapRows(double[,] a, double[] b, int r1, int r2)
    {
        if (r1 == r2)
        {
            return;
        }

        for (var c = 0; c < b.Length; c++)
        {
            (a[r1, c], a[r2, c]) = (a[r2, c], a[r1, c]);
        }

        (b[r1], b[r2]) = (b[r2], b[r1]);
    }

    /// <summary>Similarity moving the centroid to 0 and the mean distance to sqrt(2).</summary>
    private static double[]? Normalizer(IEnumerable<(double X, double Y)> points)
    {
        var pts = points.ToList();
        var cx = pts.Average(p => p.X);
        var cy = pts.Average(p => p.Y);
        var mean = pts.Average(p => Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy))));
        if (mean < 1e-12)
        {
            return null;
        }

        var s = Math.Sqrt(2) / mean;
        return [s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1];
    }

    private static (double X, double Y) Transform(double[] m, double x, double y) =>
        ((m[0] * x) + (m[1] * y) + m[2], (m[3] * x) + (m[4] * y) + m[5]);

    private static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[9];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                r[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
            }
        }

        return r;
    }

    private static double[]? Invert(double[] m)
    {
        var c00 = (m[4] * m[8]) - (m[5] * m[7]);
        var c01 = (m[5] * m[6]) - (m[3] * m[8]);
        var c02 = (m[3] * m[7]) - (m[4] * m[6]);
        var det = (m[0] * c00) + (m[1] * c01) + (m[2] * c02);
        if (Math.Abs(det) < 1e-18)
        {
            return null;
        }

        var d = 1.0 / det;
        return
        [
            c00 * d, ((m[2] * m[7]) - (m[1] * m[8])) * d, ((m[1] * m[5]) - (m[2] * m[4])) * d,
            c01 * d, ((m[0] * m[8]) - (m[2] * m[6])) * d, ((m[2] * m[3]) - (m[0] * m[5])) * d,
            c02 * d, ((m[1] * m[6]) - (m[0] * m[7])) * d, ((m[0] * m[4]) - (m[1] * m[3])) * d,
        ];
    }
}
