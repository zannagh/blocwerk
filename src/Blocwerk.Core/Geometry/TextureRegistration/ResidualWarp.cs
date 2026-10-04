// <copyright file="ResidualWarp.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>One matched pair's disagreement with the homography: normalised photo point and plane residual (mm).</summary>
/// <param name="X">Normalised photo x.</param>
/// <param name="Y">Normalised photo y.</param>
/// <param name="Da">Texture minus homography along plane a, mm.</param>
/// <param name="Db">Texture minus homography along plane b, mm.</param>
public readonly record struct WarpSample(double X, double Y, double Da, double Db);

/// <summary>
/// A smooth residual field on top of a facet's homography: where a lens distorts an oblique photo, the homography
/// that fits the facet's centre is off by a coherent amount towards its edges. The field is the Gaussian-weighted,
/// shrunk local-linear fit of the matched pairs' residuals, tabulated on a coarse grid over the photo (bilinear lookup, so a
/// placement costs a few multiplications). It shrinks to zero where pairs are few and beyond their reach (the
/// kernel's tail plus <see cref="Shrinkage"/>), so extrapolation falls back to the plain homography. It is only kept
/// when blockwise cross-validation shows it predicts pairs it was not fitted on better than the homography does
/// (<see cref="MinGain"/>): residuals that are noise, not distortion, are left alone. Deterministic, pure.
/// </summary>
public sealed class ResidualWarp
{
    /// <summary>Grid nodes per side.</summary>
    public const int Nodes = 17;

    /// <summary>Kernel width, in normalised photo units.</summary>
    public const double Sigma = 0.06;

    /// <summary>Pseudo-samples of zero residual added at every node: a node needs this much kernel weight before its mean counts.</summary>
    public const double Shrinkage = 4;

    /// <summary>Pairs further than this from the homography (mm) are mismatches, not distortion, and are not fitted.</summary>
    public const double ReachMm = 30;

    /// <summary>Largest correction the field makes, mm: the reach once per growth round (see <see cref="Grown"/>).</summary>
    public const double MaxCorrectionMm = ReachMm * (GrowthRounds + 1);

    /// <summary>Fewest fitted pairs a field is worth fitting on.</summary>
    public const int MinSamples = 200;

    /// <summary>Share by which the held-out squared residual must drop for the field to be kept.</summary>
    public const double MinGain = 0.10;

    /// <summary>Cross-validation blocks per side.</summary>
    public const int Blocks = 5;

    /// <summary>A homography whose pairs are already this close (RMS, mm) has nothing a field could correct.</summary>
    public const double NoiseFloorMm = 2;

    /// <summary>Times the fitted pairs are re-chosen around the field (see <see cref="Grown"/>).</summary>
    public const int GrowthRounds = 2;

    private const int MaxSamples = 2000;

    private readonly double x0;
    private readonly double y0;
    private readonly double step;
    private readonly double[] da;
    private readonly double[] db;

    private ResidualWarp(double x0, double y0, double step, double[] da, double[] db)
    {
        (this.x0, this.y0, this.step, this.da, this.db) = (x0, y0, step, da, db);
    }

    /// <summary>Fits the field; null when the pairs are too few or the cross-validation does not support it.</summary>
    /// <param name="samples">Every matched pair's residual against the homography.</param>
    /// <param name="minGain">The held-out improvement required (<see cref="MinGain"/>).</param>
    /// <returns>The field, or null for the plain homography.</returns>
    public static ResidualWarp? Fit(IReadOnlyList<WarpSample> samples, double minGain = MinGain)
    {
        var fitted = Grown(samples);
        if (fitted.Count < MinSamples)
        {
            return null;
        }

        var (without, with) = HeldOut(fitted);
        var floor = NoiseFloorMm * NoiseFloorMm * fitted.Count;
        return without < floor || with > (1 - minGain) * without ? null : Build(fitted);
    }

    /// <summary>The correction to add to the homography's plane position at a normalised photo point, mm.</summary>
    /// <param name="x">Normalised x.</param>
    /// <param name="y">Normalised y.</param>
    /// <returns>(a, b) correction; zero outside the field.</returns>
    public (double A, double B) Correction(double x, double y)
    {
        var (u, v) = ((x - x0) / step, (y - y0) / step);
        if (u < 0 || v < 0 || u > Nodes - 1 || v > Nodes - 1)
        {
            return (0, 0);
        }

        var (i, j) = (Math.Min((int)u, Nodes - 2), Math.Min((int)v, Nodes - 2));
        var (fu, fv) = (u - i, v - j);
        double Lerp(double[] g) =>
            ((1 - fv) * (((1 - fu) * g[(j * Nodes) + i]) + (fu * g[(j * Nodes) + i + 1])))
            + (fv * (((1 - fu) * g[((j + 1) * Nodes) + i]) + (fu * g[((j + 1) * Nodes) + i + 1])));
        return (Lerp(da), Lerp(db));
    }

    /// <summary>
    /// The pairs the field is fitted on: those within <see cref="ReachMm"/> of the homography, then — as the field takes up what
    /// the homography misses — those within reach of the homography plus the field, for <see cref="GrowthRounds"/> rounds. A
    /// homography fitted to the tightest inliers is off by more than the reach towards the photo's edges; the growth follows
    /// that drift one reach at a time and never admits a pair that disagrees with its neighbours by more than the reach.
    /// </summary>
    private static List<WarpSample> Grown(IReadOnlyList<WarpSample> samples)
    {
        var chosen = Thin(samples.Where(s => Size(s.Da, s.Db) < ReachMm).ToList());
        for (var round = 0; round < GrowthRounds && chosen.Count >= MinSamples; round++)
        {
            var field = Build(chosen);
            var next = Thin(samples.Where(s => Size(s.Da - field.Correction(s.X, s.Y).A, s.Db - field.Correction(s.X, s.Y).B) < ReachMm).ToList());
            if (next.Count < MinSamples)
            {
                break;
            }

            chosen = next;
        }

        return chosen;
    }

    private static double Size(double a, double b) => Math.Sqrt((a * a) + (b * b));

    /// <summary>The samples reduced to at most <see cref="MaxSamples"/>, deterministically (every n-th by photo position).</summary>
    private static List<WarpSample> Thin(List<WarpSample> samples)
    {
        if (samples.Count <= MaxSamples)
        {
            return samples;
        }

        var ordered = samples.OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var stride = (double)ordered.Count / MaxSamples;
        return Enumerable.Range(0, MaxSamples).Select(k => ordered[(int)(k * stride)]).ToList();
    }

    /// <summary>The summed squared residual of held-out blocks without the field and with the one fitted on the other blocks.</summary>
    private static (double Without, double With) HeldOut(List<WarpSample> samples)
    {
        int Block(WarpSample s) => (Math.Clamp((int)(s.X * Blocks), 0, Blocks - 1) * Blocks) + Math.Clamp((int)(s.Y * Blocks), 0, Blocks - 1);
        var (without, with) = (0.0, 0.0);
        foreach (var group in samples.GroupBy(Block))
        {
            var rest = samples.Where(s => Block(s) != group.Key).ToList();
            if (rest.Count < MinSamples / 2)
            {
                continue;
            }

            var field = Build(rest);
            foreach (var s in group)
            {
                var (ca, cb) = field.Correction(s.X, s.Y);
                without += (s.Da * s.Da) + (s.Db * s.Db);
                with += Math.Pow(s.Da - ca, 2) + Math.Pow(s.Db - cb, 2);
            }
        }

        return (without, with);
    }

    private static ResidualWarp Build(List<WarpSample> samples)
    {
        var margin = 2 * Sigma;
        var (minX, maxX) = (samples.Min(s => s.X) - margin, samples.Max(s => s.X) + margin);
        var (minY, maxY) = (samples.Min(s => s.Y) - margin, samples.Max(s => s.Y) + margin);
        var step = Math.Max(maxX - minX, maxY - minY) / (Nodes - 1);
        var (da, db) = (new double[Nodes * Nodes], new double[Nodes * Nodes]);
        for (var j = 0; j < Nodes; j++)
        {
            for (var i = 0; i < Nodes; i++)
            {
                var (a, b) = Node(samples, minX + (i * step), minY + (j * step));
                var scale = Math.Min(1, MaxCorrectionMm / Math.Max(Size(a, b), 1e-9));
                (da[(j * Nodes) + i], db[(j * Nodes) + i]) = (a * scale, b * scale);
            }
        }

        return new ResidualWarp(minX, minY, step, da, db);
    }

    /// <summary>
    /// The field's value at a node: a kernel-weighted local-linear fit of the residuals (linear, so the hull's border is not
    /// biased towards its inside), ridge-shrunk by <see cref="Shrinkage"/> so that thin support pulls it to zero.
    /// </summary>
    private static (double A, double B) Node(List<WarpSample> samples, double nx, double ny)
    {
        var m = new double[3, 3];
        var (ra, rb) = (new double[3], new double[3]);
        foreach (var s in samples)
        {
            var (dx, dy) = ((s.X - nx) / Sigma, (s.Y - ny) / Sigma);
            var d2 = (dx * dx) + (dy * dy);
            if (d2 > 9)
            {
                continue;
            }

            var w = Math.Exp(-d2 / 2);
            double[] t = [1, dx, dy];
            for (var p = 0; p < 3; p++)
            {
                for (var q = 0; q < 3; q++)
                {
                    m[p, q] += w * t[p] * t[q];
                }

                ra[p] += w * t[p] * s.Da;
                rb[p] += w * t[p] * s.Db;
            }
        }

        for (var p = 0; p < 3; p++)
        {
            m[p, p] += Shrinkage;
        }

        return (Solve(m, ra), Solve(m, rb));
    }

    /// <summary>The constant term of the 3 × 3 system's solution (Cramer's rule; the matrix is positive definite by the ridge).</summary>
    private static double Solve(double[,] m, double[] r)
    {
        double Det(double[,] x) =>
            (x[0, 0] * ((x[1, 1] * x[2, 2]) - (x[1, 2] * x[2, 1])))
            - (x[0, 1] * ((x[1, 0] * x[2, 2]) - (x[1, 2] * x[2, 0])))
            + (x[0, 2] * ((x[1, 0] * x[2, 1]) - (x[1, 1] * x[2, 0])));
        var first = (double[,])m.Clone();
        for (var p = 0; p < 3; p++)
        {
            first[p, 0] = r[p];
        }

        return Det(first) / Det(m);
    }
}
