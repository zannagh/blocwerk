// <copyright file="RayMath.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>Small vector helpers for the multi-view hold search (world mm).</summary>
internal static class RayMath
{
    /// <summary>The point minimising the squared distances to all rays (3×3 normal equations); null when singular.</summary>
    /// <param name="hits">The rays.</param>
    /// <returns>The point or null.</returns>
    public static double[]? Nearest(IReadOnlyList<SurfaceHit> hits)
    {
        var m = new double[3, 3];
        var r = new double[3];
        foreach (var h in hits)
        {
            var d = h.Direction;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var p = (i == j ? 1 : 0) - (d[i] * d[j]);
                    m[i, j] += p;
                    r[i] += p * h.Origin[j];
                }
            }
        }

        var det = Det(m);
        if (Math.Abs(det) < 1e-6)
        {
            return null;
        }

        var x = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var mc = (double[,])m.Clone();
            for (var i = 0; i < 3; i++)
            {
                mc[i, c] = r[i];
            }

            x[c] = Det(mc) / det;
        }

        return x;
    }

    /// <summary>The mean of the hits' wall points.</summary>
    /// <param name="hits">The hits.</param>
    /// <returns>The mean.</returns>
    public static double[] Mean(IReadOnlyList<SurfaceHit> hits) =>
        [hits.Average(h => h.World[0]), hits.Average(h => h.World[1]), hits.Average(h => h.World[2])];

    /// <summary>Distance of a point to a hit's ray.</summary>
    /// <param name="h">The hit.</param>
    /// <param name="p">The point.</param>
    /// <returns>Mm.</returns>
    public static double Distance(SurfaceHit h, double[] p)
    {
        double[] v = [p[0] - h.Origin[0], p[1] - h.Origin[1], p[2] - h.Origin[2]];
        var t = Dot(v, h.Direction);
        return Length(v, [t * h.Direction[0], t * h.Direction[1], t * h.Direction[2]]);
    }

    /// <summary>Distance between two points.</summary>
    /// <param name="a">One.</param>
    /// <param name="b">Other.</param>
    /// <returns>Mm.</returns>
    public static double Length(double[] a, double[] b) =>
        Math.Sqrt(((a[0] - b[0]) * (a[0] - b[0])) + ((a[1] - b[1]) * (a[1] - b[1])) + ((a[2] - b[2]) * (a[2] - b[2])));

    /// <summary>Dot product.</summary>
    /// <param name="a">One.</param>
    /// <param name="b">Other.</param>
    /// <returns>The product.</returns>
    public static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double Det(double[,] m) =>
        (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1]))) - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
        + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));
}
