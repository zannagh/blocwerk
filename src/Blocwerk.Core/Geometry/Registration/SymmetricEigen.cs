// <copyright file="SymmetricEigen.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>
/// Eigen-decomposition of a small real symmetric matrix by cyclic Jacobi rotations — exact to machine
/// precision for the 3×3 and 4×4 matrices the frame registration needs, with no library.
/// </summary>
internal static class SymmetricEigen
{
    private const int MaxSweeps = 64;

    /// <summary>Eigenvalues and eigenvectors (as COLUMNS of the returned matrix) of <paramref name="matrix"/>.</summary>
    public static (double[] Values, double[,] Vectors) Decompose(double[,] matrix)
    {
        var n = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            v[i, i] = 1;
        }

        for (var sweep = 0; sweep < MaxSweeps && OffDiagonal(a) > 1e-22 * (1 + Trace2(a)); sweep++)
        {
            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    Rotate(a, v, p, q);
                }
            }
        }

        var values = new double[n];
        for (var i = 0; i < n; i++)
        {
            values[i] = a[i, i];
        }

        return (values, v);
    }

    private static void Rotate(double[,] a, double[,] v, int p, int q)
    {
        if (Math.Abs(a[p, q]) < 1e-300)
        {
            return;
        }

        var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
        var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt((theta * theta) + 1));
        var c = 1 / Math.Sqrt((t * t) + 1);
        var s = t * c;
        var n = a.GetLength(0);
        for (var k = 0; k < n; k++)
        {
            var akp = a[k, p];
            var akq = a[k, q];
            a[k, p] = (c * akp) - (s * akq);
            a[k, q] = (s * akp) + (c * akq);
        }

        for (var k = 0; k < n; k++)
        {
            var apk = a[p, k];
            var aqk = a[q, k];
            a[p, k] = (c * apk) - (s * aqk);
            a[q, k] = (s * apk) + (c * aqk);
        }

        for (var k = 0; k < n; k++)
        {
            var vkp = v[k, p];
            var vkq = v[k, q];
            v[k, p] = (c * vkp) - (s * vkq);
            v[k, q] = (s * vkp) + (c * vkq);
        }
    }

    private static double OffDiagonal(double[,] a)
    {
        var sum = 0.0;
        for (var i = 0; i < a.GetLength(0); i++)
        {
            for (var j = 0; j < a.GetLength(1); j++)
            {
                sum += i == j ? 0 : a[i, j] * a[i, j];
            }
        }

        return sum;
    }

    private static double Trace2(double[,] a)
    {
        var sum = 0.0;
        for (var i = 0; i < a.GetLength(0); i++)
        {
            sum += a[i, i] * a[i, i];
        }

        return sum;
    }
}
