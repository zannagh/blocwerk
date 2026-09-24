// <copyright file="CameraResection.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>
/// Where a photo was taken from, recovered from 2D↔3D point pairs by a direct linear transform (DLT)
/// with iterative outlier trimming. Needs points off one plane (holds on ≥ 2 facets). The camera
/// centre is the null space of the 3×4 projection and does not depend on the photo's pixel scale, so
/// normalised photo coordinates serve as well as pixels.
/// </summary>
public static class CameraResection
{
    /// <summary>Fewest pairs a resection is attempted with.</summary>
    public const int MinPairs = 12;

    /// <summary>
    /// The camera centre (world mm) of the photo the <paramref name="image"/> points were seen in, or
    /// null when too few pairs, a degenerate (planar) layout, or a poor fit.
    /// </summary>
    /// <param name="world">World points, mm.</param>
    /// <param name="image">Their photo positions (any consistent 2D scale).</param>
    /// <returns>The centre.</returns>
    public static double[]? Centre(IReadOnlyList<double[]> world, IReadOnlyList<(double X, double Y)> image) =>
        Resect(world, image)?.Centre;

    /// <summary>
    /// The resection with its fit: the centre, how many pairs survived the trimming, and their median
    /// reprojection error (in the <paramref name="image"/> units); null like <see cref="Centre"/>.
    /// </summary>
    /// <param name="world">World points, mm.</param>
    /// <param name="image">Their photo positions (any consistent 2D scale).</param>
    /// <returns>The resection.</returns>
    public static CameraResectionFit? Resect(IReadOnlyList<double[]> world, IReadOnlyList<(double X, double Y)> image)
    {
        var median = double.NaN;
        var keep = Enumerable.Range(0, world.Count).ToList();
        double[]? p = null;
        for (var round = 0; round < 4 && keep.Count >= MinPairs; round++)
        {
            p = Solve(keep.Select(i => world[i]).ToList(), keep.Select(i => image[i]).ToList());
            if (p is null)
            {
                return null;
            }

            var errors = keep.Select(i => (Index: i, Error: ReprojError(p, world[i], image[i]))).ToList();
            median = errors.Select(e => e.Error).OrderBy(e => e).ElementAt(errors.Count / 2);
            var next = errors.Where(e => e.Error <= Math.Max(3 * median, 1e-6)).Select(e => e.Index).ToList();
            if (next.Count == keep.Count)
            {
                break;
            }

            keep = next;
        }

        return p is null || keep.Count < MinPairs || NullSpace(p) is not { } centre
            ? null
            : new CameraResectionFit(centre, keep.Count, median);
    }

    /// <summary>Least-squares P (p34 = 1) on Hartley-normalised points, denormalised back.</summary>
    private static double[]? Solve(List<double[]> world, List<(double X, double Y)> image)
    {
        var (mean, scale) = Normalisation(world);
        var ata = new double[11, 11];
        var atb = new double[11];
        for (var i = 0; i < world.Count; i++)
        {
            var w = world[i];
            double x = (w[0] - mean[0]) * scale, y = (w[1] - mean[1]) * scale, z = (w[2] - mean[2]) * scale;
            var (u, v) = image[i];
            Accumulate(ata, atb, [x, y, z, 1, 0, 0, 0, 0, -u * x, -u * y, -u * z], u);
            Accumulate(ata, atb, [0, 0, 0, 0, x, y, z, 1, -v * x, -v * y, -v * z], v);
        }

        var sol = LinearSolve(ata, atb);
        if (sol is null)
        {
            return null;
        }

        // P_world = P_norm · T, T = [s·I | −s·mean].
        var pn = sol.Append(1.0).ToArray();
        var p = new double[12];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                p[(r * 4) + c] = pn[(r * 4) + c] * scale;
            }

            p[(r * 4) + 3] = pn[(r * 4) + 3] - (scale * ((pn[r * 4] * mean[0]) + (pn[(r * 4) + 1] * mean[1]) + (pn[(r * 4) + 2] * mean[2])));
        }

        return p;
    }

    private static (double[] Mean, double Scale) Normalisation(List<double[]> world)
    {
        double[] mean = [world.Average(w => w[0]), world.Average(w => w[1]), world.Average(w => w[2])];
        var spread = world.Average(w => Math.Sqrt(Math.Pow(w[0] - mean[0], 2) + Math.Pow(w[1] - mean[1], 2) + Math.Pow(w[2] - mean[2], 2)));
        return (mean, spread > 1e-9 ? Math.Sqrt(3) / spread : 1);
    }

    private static void Accumulate(double[,] ata, double[] atb, double[] row, double rhs)
    {
        for (var i = 0; i < 11; i++)
        {
            atb[i] += row[i] * rhs;
            for (var j = 0; j < 11; j++)
            {
                ata[i, j] += row[i] * row[j];
            }
        }
    }

    /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
    private static double[]? LinearSolve(double[,] a, double[] b)
    {
        var n = b.Length;
        for (var col = 0; col < n; col++)
        {
            var pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(a[r, col]));
            if (Math.Abs(a[pivot, col]) < 1e-12)
            {
                return null;
            }

            for (var c = 0; c < n; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            }

            (b[col], b[pivot]) = (b[pivot], b[col]);
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

    private static double ReprojError(double[] p, double[] w, (double X, double Y) img)
    {
        var z = (p[8] * w[0]) + (p[9] * w[1]) + (p[10] * w[2]) + p[11];
        var u = ((p[0] * w[0]) + (p[1] * w[1]) + (p[2] * w[2]) + p[3]) / z;
        var v = ((p[4] * w[0]) + (p[5] * w[1]) + (p[6] * w[2]) + p[7]) / z;
        return Math.Sqrt(Math.Pow(u - img.X, 2) + Math.Pow(v - img.Y, 2));
    }

    /// <summary>The camera centre: C = −M⁻¹·p4 for P = [M | p4].</summary>
    private static double[]? NullSpace(double[] p)
    {
        var m = new double[3, 3];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                m[r, c] = p[(r * 4) + c];
            }
        }

        var c3 = LinearSolve(m, [-p[3], -p[7], -p[11]]);
        return c3 is { } centre && centre.All(double.IsFinite) ? centre : null;
    }
}
