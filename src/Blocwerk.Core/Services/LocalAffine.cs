// <copyright file="LocalAffine.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>A source→destination point pair (pixels) of two photos of the same wall.</summary>
public readonly record struct PointPair(double SrcX, double SrcY, double DstX, double DstY);

/// <summary>
/// Maps a point between two photos with a least-squares affine fitted to its nearest matched pairs (one
/// robust refit without the pairs far off the first fit). Local, so a curved or oblique photo still maps.
/// </summary>
public static class LocalAffine
{
    public const int Neighbours = 12;

    /// <summary>The mapped point, or null when fewer than three usable pairs exist.</summary>
    public static (double X, double Y)? Predict(IReadOnlyList<PointPair> pairs, double x, double y)
    {
        var near = pairs
            .OrderBy(p => Sq(p.SrcX - x) + Sq(p.SrcY - y))
            .Take(Neighbours)
            .ToList();
        var fit = Fit(near);
        if (fit is null)
        {
            return null;
        }

        var residuals = near.Select(p => Residual(fit, p)).ToList();
        var median = residuals.OrderBy(r => r).ElementAt(residuals.Count / 2);
        var kept = near.Where((_, i) => residuals[i] <= Math.Max(3 * median, 8)).ToList();
        fit = Fit(kept) ?? fit;
        return Apply(fit, x, y);
    }

    /// <summary>The local scale of the mapping at a point (destination pixels per source pixel).</summary>
    public static double? Scale(IReadOnlyList<PointPair> pairs, double x, double y)
    {
        const double Step = 50;
        var a = Predict(pairs, x, y);
        var b = Predict(pairs, x + Step, y);
        return a is null || b is null ? null : Math.Sqrt(Sq(b.Value.X - a.Value.X) + Sq(b.Value.Y - a.Value.Y)) / Step;
    }

    private static double[][]? Fit(IReadOnlyList<PointPair> pairs)
    {
        if (pairs.Count < 3)
        {
            return null;
        }

        var m = new double[3, 3];
        var bx = new double[3];
        var by = new double[3];
        foreach (var p in pairs)
        {
            double[] r = [p.SrcX, p.SrcY, 1];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    m[i, j] += r[i] * r[j];
                }

                bx[i] += r[i] * p.DstX;
                by[i] += r[i] * p.DstY;
            }
        }

        var ax = Solve(m, bx);
        var ay = Solve(m, by);
        return ax is null || ay is null ? null : [ax, ay];
    }

    private static double[]? Solve(double[,] m, double[] v)
    {
        var det = Det(m);
        if (Math.Abs(det) < 1e-9)
        {
            return null;
        }

        var result = new double[3];
        for (var col = 0; col < 3; col++)
        {
            var c = (double[,])m.Clone();
            for (var row = 0; row < 3; row++)
            {
                c[row, col] = v[row];
            }

            result[col] = Det(c) / det;
        }

        return result;
    }

    private static double Det(double[,] m) =>
        (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])))
        - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
        + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));

    private static (double X, double Y) Apply(double[][] f, double x, double y) =>
        ((f[0][0] * x) + (f[0][1] * y) + f[0][2], (f[1][0] * x) + (f[1][1] * y) + f[1][2]);

    private static double Residual(double[][] f, PointPair p)
    {
        var (x, y) = Apply(f, p.SrcX, p.SrcY);
        return Math.Sqrt(Sq(x - p.DstX) + Sq(y - p.DstY));
    }

    private static double Sq(double v) => v * v;
}
