namespace Blocwerk.HoldDetection.Alignment;

/// <summary>
/// Small managed linear-algebra helpers for homography estimation.
/// Kept dependency-free (no OpenCV / MathNet) so the alignment pipeline runs
/// anywhere SkiaSharp does.
/// </summary>
internal static class LinearAlgebra
{
    /// <summary>
    /// Returns the eigenvector (length n) associated with the smallest eigenvalue
    /// of a symmetric n x n matrix, computed via cyclic Jacobi rotations.
    /// </summary>
    public static double[] SmallestEigenvector(double[,] symmetric, int sweeps = 60)
    {
        var n = symmetric.GetLength(0);
        var a = (double[,])symmetric.Clone();
        var v = Identity(n);

        for (var sweep = 0; sweep < sweeps; sweep++)
        {
            var off = 0.0;
            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    off += a[p, q] * a[p, q];
                }
            }

            if (off < 1e-20)
            {
                break;
            }

            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-18)
                    {
                        continue;
                    }

                    Rotate(a, v, n, p, q);
                }
            }
        }

        // Diagonal of a now holds the eigenvalues; columns of v the eigenvectors.
        var minIdx = 0;
        var minVal = a[0, 0];
        for (var i = 1; i < n; i++)
        {
            if (a[i, i] < minVal)
            {
                minVal = a[i, i];
                minIdx = i;
            }
        }

        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = v[i, minIdx];
        }

        return result;
    }

    private static void Rotate(double[,] a, double[,] v, int n, int p, int q)
    {
        var app = a[p, p];
        var aqq = a[q, q];
        var apq = a[p, q];
        var phi = 0.5 * Math.Atan2(2 * apq, aqq - app);
        var c = Math.Cos(phi);
        var s = Math.Sin(phi);

        for (var i = 0; i < n; i++)
        {
            var aip = a[i, p];
            var aiq = a[i, q];
            a[i, p] = (c * aip) - (s * aiq);
            a[i, q] = (s * aip) + (c * aiq);
        }

        for (var i = 0; i < n; i++)
        {
            var api = a[p, i];
            var aqi = a[q, i];
            a[p, i] = (c * api) - (s * aqi);
            a[q, i] = (s * api) + (c * aqi);
        }

        for (var i = 0; i < n; i++)
        {
            var vip = v[i, p];
            var viq = v[i, q];
            v[i, p] = (c * vip) - (s * viq);
            v[i, q] = (s * vip) + (c * viq);
        }
    }

    private static double[,] Identity(int n)
    {
        var m = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            m[i, i] = 1.0;
        }

        return m;
    }

    /// <summary>Multiplies two row-major 3x3 matrices (a * b).</summary>
    public static double[] Mat3Mul(double[] a, double[] b)
    {
        var r = new double[9];
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                r[(row * 3) + col] =
                    (a[(row * 3) + 0] * b[(0 * 3) + col]) +
                    (a[(row * 3) + 1] * b[(1 * 3) + col]) +
                    (a[(row * 3) + 2] * b[(2 * 3) + col]);
            }
        }

        return r;
    }

    /// <summary>Inverts a row-major 3x3 matrix. Returns null if near-singular.</summary>
    public static double[]? Mat3Inverse(double[] m)
    {
        var det =
            (m[0] * ((m[4] * m[8]) - (m[5] * m[7]))) -
            (m[1] * ((m[3] * m[8]) - (m[5] * m[6]))) +
            (m[2] * ((m[3] * m[7]) - (m[4] * m[6])));
        if (Math.Abs(det) < 1e-15)
        {
            return null;
        }

        var inv = det;
        return new[]
        {
            ((m[4] * m[8]) - (m[5] * m[7])) / inv,
            ((m[2] * m[7]) - (m[1] * m[8])) / inv,
            ((m[1] * m[5]) - (m[2] * m[4])) / inv,
            ((m[5] * m[6]) - (m[3] * m[8])) / inv,
            ((m[0] * m[8]) - (m[2] * m[6])) / inv,
            ((m[2] * m[3]) - (m[0] * m[5])) / inv,
            ((m[3] * m[7]) - (m[4] * m[6])) / inv,
            ((m[1] * m[6]) - (m[0] * m[7])) / inv,
            ((m[0] * m[4]) - (m[1] * m[3])) / inv,
        };
    }
}
