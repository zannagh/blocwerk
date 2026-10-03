// <copyright file="VolumeTopRegions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// High regions of a volume's height field beyond one apex or ridge: a plateau (a flat top such as a ridge plank or a
/// truncated pyramid's top sheet: the level with the most cells in a thin height band that form one compact area) and
/// several separated peaks (volumes that were detected as one).
/// </summary>
public static class VolumeTopRegions
{
    /// <summary>A plateau at least this wide makes it the decided reading, mm.</summary>
    public const double DecidedPlateauWidthMm = 80;

    private const double MinPlateauWidthMm = 40;
    private const double MaxPlateauSlope = 0.2;
    private const double MinFill = 0.55;
    private const double MaxAboveShare = 0.08;
    private const double EdgeQuantile = 0.97;
    private const double LowestPlateauShare = 0.5;
    private const double PeakShare = 0.5;
    private const int MinPeakCells = 12;
    private const double MinPeakAcrossMm = 100;
    private const double LevelPlateauMm = 10;
    private const int MinRegionCells = 6;
    private const int MaxPeaks = 4;

    /// <summary>The plateau's outline as top vertices on its fitted plane, or null when the height field has no flat top.</summary>
    /// <param name="cells">The volume's cells (a, b, height).</param>
    /// <param name="baseRing">The base polygon (convex, counter-clockwise): the plateau's edges run parallel to it, as a sheet volume's do.</param>
    /// <param name="reference">The robust maximum height, mm.</param>
    /// <param name="cellMm">Grid cell side, mm.</param>
    /// <returns>The plateau candidate (not decided) and its width, mm; null without one.</returns>
    public static (VolumeTopCandidate Candidate, double WidthMm)? Plateau(
        IReadOnlyList<(double A, double B, double H)> cells, IReadOnlyList<(double A, double B)> baseRing, double reference, double cellMm)
    {
        var half = Math.Max(8, 0.06 * reference);
        List<(double A, double B, double H)>? best = null;
        for (var level = reference; level >= LowestPlateauShare * reference; level -= 2)
        {
            var band = cells.Where(c => Math.Abs(c.H - level) <= half).ToList();
            if (band.Count < MinRegionCells || band.Count <= (best?.Count ?? 0))
            {
                continue;
            }

            var region = Components(band, cellMm).MaxBy(r => r.Count)!;
            if (region.Count > (best?.Count ?? 0) && region.Count >= MinRegionCells && Fill(region, cellMm) >= MinFill
                && Above(cells, region, level + (2 * half), cellMm) <= MaxAboveShare * region.Count)
            {
                best = region;
            }
        }

        if (best is null || FitPlane(best) is not { } plane || Math.Sqrt((plane.Alpha * plane.Alpha) + (plane.Beta * plane.Beta)) > MaxPlateauSlope)
        {
            return null;
        }

        var outline = Parallel(best, baseRing);
        if (outline.Count < 3)
        {
            return null;
        }

        var heights = outline.Select(p => (plane.Alpha * p.A) + (plane.Beta * p.B) + plane.Gamma).ToList();
        if (heights.Max() - heights.Min() <= LevelPlateauMm)
        {
            // Level within the noise: one height keeps every side a single sheet (a tilted top splits them into triangles).
            plane = (0, 0, heights.Average());
        }

        var width = Width(outline);
        var top = outline.Select(p => (p.A, p.B, (plane.Alpha * p.A) + (plane.Beta * p.B) + plane.Gamma)).ToList();
        return width < MinPlateauWidthMm ? null : (new VolumeTopCandidate(top, false, "plateau", plane), width);
    }

    /// <summary>
    /// The separated high regions (cells above half the robust maximum, connected) big enough to be a volume's top (at
    /// least 12 cells and 100 mm across, so a hold on a slope is not one), largest first; fewer than two means one peak.
    /// </summary>
    /// <param name="cells">The volume's cells (a, b, height).</param>
    /// <param name="reference">The robust maximum height, mm.</param>
    /// <param name="cellMm">Grid cell side, mm.</param>
    /// <returns>The regions, at most four.</returns>
    public static List<List<(double A, double B, double H)>> Peaks(IReadOnlyList<(double A, double B, double H)> cells, double reference, double cellMm) =>
        Components(cells.Where(c => c.H >= PeakShare * reference).ToList(), cellMm)
            .Where(r => r.Count >= MinPeakCells && Across(r) >= MinPeakAcrossMm).OrderByDescending(r => r.Count).Take(MaxPeaks).ToList();

    /// <summary>The least-squares plane h = α·a + β·b + γ through the cells; null when degenerate.</summary>
    /// <param name="cells">The cells.</param>
    /// <returns>The plane.</returns>
    public static (double Alpha, double Beta, double Gamma)? FitPlane(IReadOnlyList<(double A, double B, double H)> cells)
    {
        double ca = cells.Average(c => c.A), cb = cells.Average(c => c.B), ch = cells.Average(c => c.H);
        double saa = 0, sab = 0, sbb = 0, sah = 0, sbh = 0;
        foreach (var c in cells)
        {
            double a = c.A - ca, b = c.B - cb, h = c.H - ch;
            (saa, sab, sbb, sah, sbh) = (saa + (a * a), sab + (a * b), sbb + (b * b), sah + (a * h), sbh + (b * h));
        }

        var det = (saa * sbb) - (sab * sab);
        if (Math.Abs(det) < 1e-6)
        {
            return null;
        }

        var alpha = ((sah * sbb) - (sbh * sab)) / det;
        var beta = ((sbh * saa) - (sah * sab)) / det;
        return (alpha, beta, ch - (alpha * ca) - (beta * cb));
    }

    /// <summary>Connected groups of cells (neighbours up to two cells apart, so one missing cell does not split a region).</summary>
    private static List<List<(double A, double B, double H)>> Components(List<(double A, double B, double H)> cells, double cellMm)
    {
        var key = cells.Select(c => ((int)Math.Floor(c.A / cellMm), (int)Math.Floor(c.B / cellMm))).ToList();
        var index = new Dictionary<(int I, int J), int>();
        for (var k = 0; k < cells.Count; k++)
        {
            index.TryAdd(key[k], k);
        }

        var seen = new bool[cells.Count];
        var groups = new List<List<(double A, double B, double H)>>();
        for (var s = 0; s < cells.Count; s++)
        {
            if (seen[s])
            {
                continue;
            }

            seen[s] = true;
            var group = new List<(double A, double B, double H)>();
            var queue = new Queue<int>([s]);
            while (queue.TryDequeue(out var k))
            {
                group.Add(cells[k]);
                foreach (var m in Neighbours(index, key[k]).Where(m => !seen[m]))
                {
                    seen[m] = true;
                    queue.Enqueue(m);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static IEnumerable<int> Neighbours(Dictionary<(int I, int J), int> index, (int I, int J) at)
    {
        for (var di = -2; di <= 2; di++)
        {
            for (var dj = -2; dj <= 2; dj++)
            {
                if ((di * di) + (dj * dj) <= 4 && index.TryGetValue((at.I + di, at.J + dj), out var m))
                {
                    yield return m;
                }
            }
        }
    }

    /// <summary>The larger side of the cells' bounding box.</summary>
    private static double Across(List<(double A, double B, double H)> cells) =>
        Math.Max(cells.Max(c => c.A) - cells.Min(c => c.A), cells.Max(c => c.B) - cells.Min(c => c.B));

    /// <summary>The convex hull of the cells' corners.</summary>
    private static List<(double A, double B)> Corners(List<(double A, double B, double H)> cells, double cellMm)
    {
        var h = cellMm / 2;
        return PlanePolygon.ConvexHull(cells.SelectMany(c => new[] { (c.A - h, c.B - h), (c.A + h, c.B - h), (c.A - h, c.B + h), (c.A + h, c.B + h) }));
    }

    /// <summary>How much of the region's convex hull its cells fill (the band round a pyramid's slopes is a ring and fills little).</summary>
    private static double Fill(List<(double A, double B, double H)> cells, double cellMm)
    {
        var area = PlanePolygon.Area(Corners(cells, cellMm));
        return area <= 0 ? 0 : cells.Count * cellMm * cellMm / area;
    }

    /// <summary>The region's outline with edges parallel to the base's: each at the 97th percentile of the cells along its normal.</summary>
    private static List<(double A, double B)> Parallel(List<(double A, double B, double H)> region, IReadOnlyList<(double A, double B)> baseRing)
    {
        var pad = 10_000 + region.Max(c => Math.Abs(c.A) + Math.Abs(c.B));
        List<(double A, double B)> ring = [(-pad, -pad), (pad, -pad), (pad, pad), (-pad, pad)];
        for (var i = 0; i < baseRing.Count && ring.Count >= 3; i++)
        {
            var n = VolumeHull.Outward(baseRing[i], baseRing[(i + 1) % baseRing.Count]);
            ring = VolumeRings.ClipHalfPlane(ring, n, VolumeTop.Quantile(region.Select(c => (n.A * c.A) + (n.B * c.B)).ToList(), EdgeQuantile));
        }

        return ring.Count < 3 ? [] : PlanePolygon.ConvexHull(ring);
    }

    /// <summary>Cells inside the region's outline clearly higher than its band (a pyramid's tip inside a ring; a few are holds on a plank).</summary>
    private static int Above(IReadOnlyList<(double A, double B, double H)> cells, List<(double A, double B, double H)> region, double over, double cellMm)
    {
        var hull = Corners(region, cellMm);
        return cells.Count(c => c.H > over && PlanePolygon.Contains(hull, (c.A, c.B)));
    }

    /// <summary>The smallest extent of a convex outline over its edge directions.</summary>
    private static double Width(List<(double A, double B)> ring)
    {
        var width = double.PositiveInfinity;
        for (var i = 0; i < ring.Count; i++)
        {
            var n = VolumeHull.Outward(ring[i], ring[(i + 1) % ring.Count]);
            var (lo, hi) = PlanePolygon.Extent(ring, n.A, n.B);
            width = Math.Min(width, hi - lo);
        }

        return width;
    }
}
