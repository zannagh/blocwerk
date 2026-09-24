// <copyright file="WallSurfaceFit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// Height above the wall's own surface: a robust quadratic h(a, b) (Tukey IRLS) fitted to a facet's points
/// absorbs the splat's residual tilt and bow, so what stays is what stands proud of the wall.
/// </summary>
public static class WallSurfaceFit
{
    private const int Terms = 6;
    private const int Iterations = 8;
    private const double ScaleMm = 20;
    private const double StartBandMm = 60;

    /// <summary>The heights relative to the fitted surface.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <param name="h">Off the facet plane, mm.</param>
    /// <returns>h minus the surface, per point.</returns>
    public static double[] Relative(IReadOnlyList<double> a, IReadOnlyList<double> b, IReadOnlyList<double> h)
    {
        var n = h.Count;
        var w = new double[n];
        for (var i = 0; i < n; i++)
        {
            w[i] = Math.Abs(h[i]) < StartBandMm ? 1 : 0;
        }

        var c = 4.685 * ScaleMm;
        var coef = new double[Terms];
        for (var it = 0; it < Iterations; it++)
        {
            coef = Solve(a, b, h, w) ?? coef;
            for (var i = 0; i < n; i++)
            {
                var r = h[i] - Eval(coef, a[i], b[i]);
                var u = r / c;
                w[i] = Math.Abs(r) < c ? (1 - (u * u)) * (1 - (u * u)) : 0;
            }
        }

        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = h[i] - Eval(coef, a[i], b[i]);
        }

        return result;
    }

    private static void Design(double a, double b, Span<double> x)
    {
        var p = a / 1000;
        var q = b / 1000;
        x[0] = 1;
        x[1] = p;
        x[2] = q;
        x[3] = p * p;
        x[4] = p * q;
        x[5] = q * q;
    }

    private static double Eval(double[] coef, double a, double b)
    {
        Span<double> x = stackalloc double[Terms];
        Design(a, b, x);
        var s = 0.0;
        for (var k = 0; k < Terms; k++)
        {
            s += coef[k] * x[k];
        }

        return s;
    }

    /// <summary>Weighted least squares via the normal equations; null when singular.</summary>
    private static double[]? Solve(IReadOnlyList<double> a, IReadOnlyList<double> b, IReadOnlyList<double> h, double[] w)
    {
        var m = new double[Terms, Terms + 1];
        Span<double> x = stackalloc double[Terms];
        for (var i = 0; i < h.Count; i++)
        {
            if (w[i] <= 0)
            {
                continue;
            }

            Design(a[i], b[i], x);
            for (var r = 0; r < Terms; r++)
            {
                for (var k = 0; k < Terms; k++)
                {
                    m[r, k] += w[i] * x[r] * x[k];
                }

                m[r, Terms] += w[i] * x[r] * h[i];
            }
        }

        return Gauss(m);
    }

    private static double[]? Gauss(double[,] m)
    {
        for (var col = 0; col < Terms; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < Terms; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                return null;
            }

            for (var k = 0; k <= Terms; k++)
            {
                (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            }

            for (var r = 0; r < Terms; r++)
            {
                if (r == col)
                {
                    continue;
                }

                var f = m[r, col] / m[col, col];
                for (var k = col; k <= Terms; k++)
                {
                    m[r, k] -= f * m[col, k];
                }
            }
        }

        var result = new double[Terms];
        for (var r = 0; r < Terms; r++)
        {
            result[r] = m[r, Terms] / m[r, r];
        }

        return result;
    }
}
