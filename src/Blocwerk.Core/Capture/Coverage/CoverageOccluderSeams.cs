// <copyright file="CoverageOccluderSeams.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Where a facet really is: its region rectangle cut along the seams with its neighbours where the facet's own voting
/// markers (<see cref="GeometryKernel.VotingCorners"/>) all lie on one side, so a triangle segment (side wall, closing
/// piece) keeps only its real half, and along its outline's fold clips (SfM models, which have no markers). A seam is only
/// used where the neighbour really is (the seam runs, on average, within <see cref="MaxSeamGapMm"/> of the neighbour's
/// region). The geometry kernel's facet shape (<c>docs/geometry-kernel.md</c>), as the wall textures' <c>occlusion.py</c>.
/// </summary>
internal static class CoverageOccluderSeams
{
    /// <summary>Planes closer to parallel than this (sine of their angle, ~10°) give no reliable seam.</summary>
    public const double MinPlaneAngleSin = GeometryKernel.MinPlaneAngleSin;

    /// <summary>A seam counts only when it lies, on average, within this of the neighbour's region, mm.</summary>
    public const double MaxSeamGapMm = GeometryKernel.MaxSeamGapMm;

    /// <summary>A marker corner this close to a seam still counts as on either side, mm.</summary>
    public const double MarkerSideTolMm = GeometryKernel.MarkerSideTolMm;

    /// <summary>One occluder per facet, each cut by its marker-confirmed seams and its fold clips.</summary>
    /// <param name="facets">The facets.</param>
    /// <returns>The occluders, in the facets' order.</returns>
    public static IReadOnlyList<CoverageOccluder> Build(IReadOnlyList<CoverageFacet> facets) =>
        facets.Select(g => new CoverageOccluder(g, [.. HalfPlanes(g, facets), .. g.FoldCuts ?? []])).ToList();

    private static List<(double Alpha, double Beta, double Gamma)> HalfPlanes(CoverageFacet g, IReadOnlyList<CoverageFacet> facets)
    {
        var result = new List<(double, double, double)>();
        var corners = g.MarkerCorners ?? [];
        if (corners.Count == 0)
        {
            return result;
        }

        var r = g.Region;
        (double A, double B)[] rect = [(r.AMin, r.BMin), (r.AMax, r.BMin), (r.AMax, r.BMax), (r.AMin, r.BMax)];
        foreach (var h in facets)
        {
            if (h.Id == g.Id || Seam(g, h) is not { } seam)
            {
                continue;
            }

            var (al, be, ga) = seam;
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                if (corners.All(c => sign * ((al * c[0]) + (be * c[1]) - ga) >= -MarkerSideTolMm)
                    && rect.Any(c => sign * ((al * c.A) + (be * c.B) - ga) < -MarkerSideTolMm))
                {
                    if (SeamGapMm(g, h, seam) <= MaxSeamGapMm)
                    {
                        result.Add((sign * al, sign * be, sign * ga));
                    }

                    break;
                }
            }
        }

        return result;
    }

    /// <summary>The line in g's plane where it meets h's plane, as a unit (alpha, beta, gamma); null when near-parallel.</summary>
    private static (double Alpha, double Beta, double Gamma)? Seam(CoverageFacet g, CoverageFacet h)
    {
        var n = h.Frame.Normal;
        double al = Dot(n, g.Frame.U), be = Dot(n, g.Frame.V);
        var norm = Math.Sqrt((al * al) + (be * be));
        if (norm < MinPlaneAngleSin)
        {
            return null;
        }

        var o = h.Frame.Origin;
        var og = g.Frame.Origin;
        var ga = Dot(n, [o[0] - og[0], o[1] - og[1], o[2] - og[2]]);
        return (al / norm, be / norm, ga / norm);
    }

    /// <summary>Mean distance of the seam's part inside g's region from h's region (measured in h's plane), mm.</summary>
    private static double SeamGapMm(CoverageFacet g, CoverageFacet h, (double Alpha, double Beta, double Gamma) seam)
    {
        var (al, be, ga) = seam;
        var r = g.Region;
        double p0a = al * ga, p0b = be * ga, da = -be, db = al;
        var ts = new List<double>();
        if (Math.Abs(da) > 1e-9)
        {
            ts.Add((r.AMin - p0a) / da);
            ts.Add((r.AMax - p0a) / da);
        }

        if (Math.Abs(db) > 1e-9)
        {
            ts.Add((r.BMin - p0b) / db);
            ts.Add((r.BMax - p0b) / db);
        }

        ts.Sort();
        double lo = ts[(ts.Count / 2) - 1], hi = ts[ts.Count / 2], total = 0;
        var count = 0;
        for (var i = 0; i <= 10; i++)
        {
            var t = lo + ((hi - lo) * i / 10);
            double a = p0a + (t * da), b = p0b + (t * db);
            if (a < r.AMin - 1 || a > r.AMax + 1 || b < r.BMin - 1 || b > r.BMax + 1)
            {
                continue;
            }

            var w = g.Frame.ToWorld(a, b);
            var (ha, hb, _) = FacetCloud.Local(h.Frame, w[0], w[1], w[2]);
            total += RectDistance(h.Region, ha, hb);
            count++;
        }

        return count == 0 ? double.PositiveInfinity : total / count;
    }

    private static double RectDistance(PlaneRectMm r, double a, double b) =>
        Math.Sqrt(Math.Pow(Math.Max(Math.Max(r.AMin - a, 0), a - r.AMax), 2) + Math.Pow(Math.Max(Math.Max(r.BMin - b, 0), b - r.BMax), 2));

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
