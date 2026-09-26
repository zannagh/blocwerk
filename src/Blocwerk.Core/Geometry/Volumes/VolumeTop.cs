// <copyright file="VolumeTop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// The detected high points of a volume's height field: the cells near its robust maximum (≥ 90 % of the 99.5th
/// percentile, so single noisy cells do not count), a line fitted through them (principal axis), and the decision:
/// when that band is longer than a quarter of the base along the same line it is a roof's ridge (its two ends are the
/// top vertices), else one apex (a pyramid). Heights come from a high percentile of the band, not the single top cell.
/// </summary>
public static class VolumeTop
{
    /// <summary>A band longer than this share of the base's extent along it is a ridge.</summary>
    public const double RidgeShare = 0.25;

    private const double BandShare = 0.9;
    private const double RefQuantile = 0.995;
    private const double TopQuantile = 0.9;
    private const double LevelRidgeMm = 15;

    /// <summary>The top vertices: one (apex) or two (ridge ends); empty without cells.</summary>
    /// <param name="cells">The volume's cells (a, b, height).</param>
    /// <param name="baseRing">The base polygon.</param>
    /// <returns>The top vertices.</returns>
    public static List<(double A, double B, double H)> Find(IReadOnlyList<(double A, double B, double H)> cells, IReadOnlyList<(double A, double B)> baseRing) =>
        Candidates(cells, baseRing).FirstOrDefault().Top ?? [];

    /// <summary>
    /// Both readings of the high points: first the one the band's length decides (ridge when longer than
    /// <see cref="RidgeShare"/> of the base, else apex), then the other one (when the band allows a ridge at all).
    /// </summary>
    /// <param name="cells">The volume's cells (a, b, height).</param>
    /// <param name="baseRing">The base polygon.</param>
    /// <returns>The candidates, the decided one first.</returns>
    public static List<(List<(double A, double B, double H)> Top, bool Decided)> Candidates(
        IReadOnlyList<(double A, double B, double H)> cells, IReadOnlyList<(double A, double B)> baseRing)
    {
        if (cells.Count == 0)
        {
            return [];
        }

        var reference = Quantile(cells.Select(c => c.H).ToList(), RefQuantile);
        var band = cells.Where(c => c.H >= BandShare * reference).ToList();
        if (band.Count < 3)
        {
            band = cells.OrderByDescending(c => c.H).Take(3).ToList();
        }

        double ca = band.Average(c => c.A), cb = band.Average(c => c.B);
        var (ea, eb) = PrincipalAxis(band, ca, cb);
        var s = band.Select(c => ((c.A - ca) * ea) + ((c.B - cb) * eb)).ToList();
        double lo = Quantile(s, 0.05), hi = Quantile(s, 0.95);
        var (baseMin, baseMax) = PlanePolygon.Extent(baseRing, ea, eb);
        List<(double A, double B, double H)> apex = [(ca, cb, Quantile(band.Select(c => c.H).ToList(), TopQuantile))];
        if (band.Count < 6)
        {
            return [(apex, true)];
        }

        var mid = (lo + hi) / 2;
        var hLo = Quantile(band.Where((_, k) => s[k] <= mid).Select(c => c.H).ToList(), TopQuantile);
        var hHi = Quantile(band.Where((_, k) => s[k] > mid).Select(c => c.H).ToList(), TopQuantile);
        if (Math.Abs(hLo - hHi) < LevelRidgeMm)
        {
            // A level ridge: one height keeps a long side that runs along it one flat sheet.
            hLo = hHi = (hLo + hHi) / 2;
        }

        List<(double A, double B, double H)> ridge = [(ca + (lo * ea), cb + (lo * eb), hLo), (ca + (hi * ea), cb + (hi * eb), hHi)];
        return hi - lo > RidgeShare * (baseMax - baseMin) ? [(ridge, true), (apex, false)] : [(apex, true), (ridge, false)];
    }

    /// <summary>The <paramref name="q"/> quantile (nearest rank) of the values; 0 when empty.</summary>
    /// <param name="values">The values.</param>
    /// <param name="q">0–1.</param>
    /// <returns>The quantile.</returns>
    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return sorted[(int)Math.Round(q * (sorted.Count - 1))];
    }

    /// <summary>The unit direction of largest spread (2×2 covariance, closed form).</summary>
    private static (double A, double B) PrincipalAxis(List<(double A, double B, double H)> band, double ca, double cb)
    {
        double saa = 0, sbb = 0, sab = 0;
        foreach (var c in band)
        {
            double da = c.A - ca, db = c.B - cb;
            saa += da * da;
            sbb += db * db;
            sab += da * db;
        }

        var angle = 0.5 * Math.Atan2(2 * sab, saa - sbb);
        return (Math.Cos(angle), Math.Sin(angle));
    }
}
