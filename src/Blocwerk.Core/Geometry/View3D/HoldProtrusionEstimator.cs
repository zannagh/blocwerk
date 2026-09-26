// <copyright file="HoldProtrusionEstimator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>A hold to measure: its facet, plane centre, outline (relative mm) and outline key.</summary>
/// <param name="Id">Hold id.</param>
/// <param name="FacetId">Facet the hold sits on.</param>
/// <param name="A">Plane centre along u, mm.</param>
/// <param name="B">Plane centre along v, mm.</param>
/// <param name="Outline">Footprint / outline ring relative to (A, B), <c>[da, db]</c> per vertex.</param>
/// <param name="OutlineKey">The hold's <see cref="HoldFootprint.KeyOf"/>.</param>
public sealed record ProtrusionHold(Guid Id, string FacetId, double A, double B, IReadOnlyList<double[]> Outline, string OutlineKey);

/// <summary>
/// Measures how far each hold stands out of its facet from the photo-real scene's splat centres (in the
/// wall world, after the splat's fine alignment). Per hold: the points inside its footprint (grown a
/// little, the footprint is the CONTACT area) give the body height (80th percentile) and the apex
/// (the top tenth's centroid, 95th percentile height); the points in a ring just around it give the
/// surface it is bolted to (the wall, or a volume). A hold on a volume is moved to where the photo ray
/// meets the volume (<see cref="HoldVolumeRemap"/>, when camera centres are given) and measured there.
/// Holds with too few points get <see cref="HoldProtrusion.Estimate"/>.
/// </summary>
public static class HoldProtrusionEstimator
{
    /// <summary>Fewest points inside a footprint for a measurement (splat centres).</summary>
    public const int MinPoints = 15;

    private const double CellMm = 50;
    private const double RingInnerMm = 25;
    private const double MaxOffPlaneMm = 400;
    private const double OtherSurfaceMm = 30;

    /// <summary>A "surface" further out than this is something in front of the wall (a mat, a person), not a volume.</summary>
    private const double MaxBaseMm = 200;

    /// <summary>Measures <paramref name="holds"/> against <paramref name="points"/> (world mm, x/y/z triples).</summary>
    /// <param name="points">Splat centres in the wall world, already filtered to surface-like splats.</param>
    /// <param name="frames">The facets by id.</param>
    /// <param name="holds">The holds to measure.</param>
    /// <param name="cameras">Solved capture camera centres (world mm) for moving holds on volumes; none: no move.</param>
    /// <param name="extents">The facets' plane rectangles, to tell a neighbouring facet's surface from a volume.</param>
    /// <param name="tuning">What the points are (splat centres by default, or sparse points: <see cref="HoldProtrusionTuning.Sparse"/>).</param>
    /// <returns>A protrusion per hold whose facet is known.</returns>
    public static Dictionary<Guid, HoldProtrusion> Measure(
        IReadOnlyList<(float X, float Y, float Z)> points,
        IReadOnlyDictionary<string, FacetFrame> frames,
        IEnumerable<ProtrusionHold> holds,
        IReadOnlyList<double[]>? cameras = null,
        IReadOnlyDictionary<string, PlaneRectMm>? extents = null,
        HoldProtrusionTuning? tuning = null)
    {
        tuning ??= HoldProtrusionTuning.Splat;
        var grids = new Dictionary<string, Dictionary<(int I, int J), List<(double A, double B, double D)>>>();
        var result = new Dictionary<Guid, HoldProtrusion>();
        foreach (var hold in holds)
        {
            if (!frames.TryGetValue(hold.FacetId, out var frame) || hold.Outline.Count < 3)
            {
                continue;
            }

            if (!grids.TryGetValue(hold.FacetId, out var grid))
            {
                var others = (extents ?? new Dictionary<string, PlaneRectMm>())
                    .Where(e => e.Key != hold.FacetId && frames.ContainsKey(e.Key))
                    .Select(e => (frames[e.Key], e.Value))
                    .ToList();
                grid = GridOf(points, frame, others);
                grids[hold.FacetId] = grid;
            }

            var p = MeasureOne(hold, grid, tuning);
            if (p.OnVolume && cameras is { Count: > 0 } && HoldVolumeRemap.Shift(hold.A, hold.B, frame, grid, CellMm, cameras) is { } shift)
            {
                var moved = MeasureOne(hold with { A = hold.A + shift.A, B = hold.B + shift.B }, grid, tuning);
                if (moved.Source != HoldProtrusionSource.Estimate)
                {
                    p = moved with
                    {
                        ApexA = Math.Round(moved.ApexA + shift.A, 1),
                        ApexB = Math.Round(moved.ApexB + shift.B, 1),
                        ShiftA = Math.Round(shift.A, 1),
                        ShiftB = Math.Round(shift.B, 1),
                    };
                }
            }

            result[hold.Id] = p;
        }

        return result;
    }

    private static HoldProtrusion MeasureOne(
        ProtrusionHold hold, Dictionary<(int I, int J), List<(double A, double B, double D)>> grid, HoldProtrusionTuning tuning)
    {
        var poly = hold.Outline.Select(p => new[] { p[0] + hold.A, p[1] + hold.B }).ToList();
        var inner = Grow(poly, tuning.InsideGrowMm);
        var ringIn = Grow(poly, RingInnerMm);
        var ringOut = Grow(poly, tuning.RingOuterMm);
        var inside = new List<(double A, double B, double D)>();
        var ring = new List<double>();
        foreach (var p in Near(grid, ringOut))
        {
            if (Contains(inner, p.A, p.B))
            {
                inside.Add(p);
            }
            else if (Contains(ringOut, p.A, p.B) && !Contains(ringIn, p.A, p.B))
            {
                ring.Add(p.D);
            }
        }

        var width = poly.Max(p => p[0]) - poly.Min(p => p[0]);
        var height = poly.Max(p => p[1]) - poly.Min(p => p[1]);
        var baseMm = ring.Count >= tuning.MinRingPoints ? Percentile(ring.Order().ToArray(), 0.3) : 0;
        if (inside.Count < tuning.MinPoints || baseMm > MaxBaseMm)
        {
            return HoldProtrusion.Estimate(width, height, hold.OutlineKey);
        }

        var heights = inside.Select(p => p.D).Order().ToArray();
        var body = Percentile(heights, tuning.BodyQuantile);
        var apexH = Percentile(heights, 0.95);
        var topCut = Percentile(heights, 0.9);
        var top = inside.Where(p => p.D >= topCut).ToList();
        return new HoldProtrusion(
            tuning.Source,
            inside.Count,
            Math.Round(baseMm, 1),
            Math.Round(body, 1),
            Math.Round(top.Average(p => p.A) - hold.A, 1),
            Math.Round(top.Average(p => p.B) - hold.B, 1),
            Math.Round(apexH, 1),
            hold.OutlineKey);
    }

    /// <summary>
    /// The points near one facet, in its (a, b, height) frame, bucketed on a coarse grid. A point lying on
    /// another facet's surface (inside its extent, nearer its plane, within <see cref="OtherSurfaceMm"/>) is
    /// left out: near a fold, the neighbouring facet would otherwise read as a volume under the holds along the edge.
    /// </summary>
    private static Dictionary<(int I, int J), List<(double A, double B, double D)>> GridOf(
        IReadOnlyList<(float X, float Y, float Z)> points, FacetFrame frame, IReadOnlyList<(FacetFrame Frame, PlaneRectMm Extent)> others)
    {
        var grid = new Dictionary<(int I, int J), List<(double A, double B, double D)>>();
        double[] o = frame.Origin, u = frame.U, v = frame.V, n = frame.Normal;
        foreach (var (x, y, z) in points)
        {
            double rx = x - o[0], ry = y - o[1], rz = z - o[2];
            var d = (rx * n[0]) + (ry * n[1]) + (rz * n[2]);
            if (Math.Abs(d) > MaxOffPlaneMm || OnOtherSurface(x, y, z, Math.Abs(d), others))
            {
                continue;
            }

            var a = (rx * u[0]) + (ry * u[1]) + (rz * u[2]);
            var b = (rx * v[0]) + (ry * v[1]) + (rz * v[2]);
            var key = ((int)Math.Floor(a / CellMm), (int)Math.Floor(b / CellMm));
            if (!grid.TryGetValue(key, out var list))
            {
                list = [];
                grid[key] = list;
            }

            list.Add((a, b, d));
        }

        return grid;
    }

    private static bool OnOtherSurface(double x, double y, double z, double ownMm, IReadOnlyList<(FacetFrame Frame, PlaneRectMm Extent)> others)
    {
        foreach (var (f, e) in others)
        {
            double rx = x - f.Origin[0], ry = y - f.Origin[1], rz = z - f.Origin[2];
            var d = Math.Abs((rx * f.Normal[0]) + (ry * f.Normal[1]) + (rz * f.Normal[2]));
            if (d >= OtherSurfaceMm || d >= ownMm)
            {
                continue;
            }

            var a = (rx * f.U[0]) + (ry * f.U[1]) + (rz * f.U[2]);
            var b = (rx * f.V[0]) + (ry * f.V[1]) + (rz * f.V[2]);
            if (a >= e.AMin && a <= e.AMax && b >= e.BMin && b <= e.BMax)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(double A, double B, double D)> Near(
        Dictionary<(int I, int J), List<(double A, double B, double D)>> grid, List<double[]> poly)
    {
        var a0 = (int)Math.Floor(poly.Min(p => p[0]) / CellMm);
        var a1 = (int)Math.Floor(poly.Max(p => p[0]) / CellMm);
        var b0 = (int)Math.Floor(poly.Min(p => p[1]) / CellMm);
        var b1 = (int)Math.Floor(poly.Max(p => p[1]) / CellMm);
        for (var i = a0; i <= a1; i++)
        {
            for (var j = b0; j <= b1; j++)
            {
                if (grid.TryGetValue((i, j), out var list))
                {
                    foreach (var p in list)
                    {
                        yield return p;
                    }
                }
            }
        }
    }

    /// <summary>The ring pushed out radially from its centroid by <paramref name="mm"/>.</summary>
    private static List<double[]> Grow(List<double[]> poly, double mm)
    {
        var ca = poly.Average(p => p[0]);
        var cb = poly.Average(p => p[1]);
        return poly.Select(p =>
        {
            double da = p[0] - ca, db = p[1] - cb;
            var r = Math.Sqrt((da * da) + (db * db)) + 1e-9;
            return new[] { ca + (da * (r + mm) / r), cb + (db * (r + mm) / r) };
        }).ToList();
    }

    private static bool Contains(List<double[]> poly, double a, double b)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double ai = poly[i][0], bi = poly[i][1], aj = poly[j][0], bj = poly[j][1];
            if ((bi > b) != (bj > b) && a < ((aj - ai) * (b - bi) / (bj - bi)) + ai)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double Percentile(double[] sorted, double q)
    {
        var x = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(x);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (x - lo));
    }
}
