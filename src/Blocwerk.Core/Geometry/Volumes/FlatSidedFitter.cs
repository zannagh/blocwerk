// <copyright file="FlatSidedFitter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>A flat-sided shape fitted to a volume's height field.</summary>
/// <param name="Polyhedron">The faces.</param>
/// <param name="RmsMm">Height difference to the measured height field inside the outline (trimmed RMS), mm.</param>
/// <param name="IsGood">Whether the flat sides describe the measurement well enough to use them without being asked.</param>
public sealed record FlatSidedFit(VolumePolyhedron Polyhedron, double RmsMm, bool IsGood);

/// <summary>
/// "Has flat sides": the owner's volumes are pyramids and roofs of flat wood sheets, so instead of the noisy height
/// field the volume becomes the convex hull of where it meets the wall (the detected outline simplified with
/// Douglas–Peucker, ~20 mm, then each edge moved out to where its side's fitted slope reaches the wall) and its
/// detected high points (<see cref="VolumeTop"/>: an apex, a ridge, a flat top's outline or the high points of several
/// peaks). Everything in between is a flat face.
/// </summary>
public static class FlatSidedFitter
{
    /// <summary>Douglas–Peucker tolerance for the outline, mm.</summary>
    public const double SimplifyMm = 20;

    /// <summary>A fit within this trimmed RMS (or <see cref="GoodRmsShare"/> of the height, if larger) is good, mm.</summary>
    public const double GoodRmsMm = 12;

    /// <summary>See <see cref="GoodRmsMm"/>.</summary>
    public const double GoodRmsShare = 0.12;

    /// <summary>
    /// A pyramid or a roof (the owner's volumes: flat plywood sheets) is also good within this, mm: a volume in the middle of
    /// a wall is photographed from fewer angles and its splat is noisier (on The Attic, 2026-09-30, a clear roof fitted to 14.9 mm).
    /// </summary>
    public const double LooseRmsMm = 16;

    private const int Passes = 3;
    private const double SlackMm = 2;
    private const double SlackShare = 0.1;
    private const int MinCells = 12;
    private const double InsetMm = 20;

    /// <summary>Shorter base edges are cut-off corners (a volume side is at least ~190 mm), mm.</summary>
    private const double MinEdgeMm = 2.5 * SimplifyMm;

    /// <summary>Outline tolerances tried, mm: the ~20 mm of a clean outline up to the coarse one of a noisy scan.</summary>
    private static readonly double[] Tolerances = [SimplifyMm, 30, 45, 60];

    /// <summary>The flat-sided shape of a volume, or null when its height field is too small or flat.</summary>
    /// <param name="field">The volume's height field.</param>
    /// <param name="footprint">Its detected outline on the facet.</param>
    /// <returns>The fit or null.</returns>
    public static FlatSidedFit? Fit(VolumeSurface field, IReadOnlyList<(double A, double B)> footprint)
    {
        var cells = Cells(field, footprint);
        if (cells.Count < MinCells)
        {
            return null;
        }

        // The simplest shape that describes the measurement about as well as the best one: fewer sides first (the
        // outline simplified more coarsely), and the other reading of the top (apex / ridge) only when clearly better.
        var candidates = new List<(FlatSidedFit Fit, bool Decided)>();
        foreach (var tolerance in Tolerances)
        {
            var outline = VolumeRings.DropShortEdges(VolumeRings.Simplify(footprint, tolerance), MinEdgeMm);
            if (outline.Count < 3)
            {
                continue;
            }

            foreach (var top in VolumeTop.Candidates(cells, outline, field.Grid.CellMm))
            {
                if (top.Top.Max(t => t.H) >= 10 && FitOne(outline, top, cells) is { } fit)
                {
                    candidates.Add((fit, top.Decided));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Separated peaks are not smoothed over by one apex: all of them go into the hull, and the list says so.
        if (candidates.Any(c => c.Decided && c.Fit.Polyhedron.Shape == "multi-peak"))
        {
            candidates = candidates.Where(c => c.Fit.Polyhedron.Shape == "multi-peak").ToList();
        }

        // A good fit beats a poor one with fewer sides.
        if (candidates.Any(c => c.Fit.IsGood))
        {
            candidates = candidates.Where(c => c.Fit.IsGood).ToList();
        }

        var best = candidates.Min(c => c.Fit.RmsMm);
        var slack = Math.Max(SlackMm, SlackShare * best);
        return candidates.Where(c => c.Fit.RmsMm <= best + slack)
            .OrderBy(c => Complexity(c.Fit.Polyhedron))
            .ThenBy(c => c.Fit.RmsMm + (c.Decided ? 0 : slack))
            .First().Fit;
    }

    /// <summary>
    /// How much a shape assumes: its flat sides plus every top vertex beyond an apex, so a ridge or a flat top has to fit
    /// clearly better than the pyramid under it (a narrow plateau's corners otherwise pass for one side more).
    /// </summary>
    /// <param name="polyhedron">The fitted shape.</param>
    /// <returns>The complexity.</returns>
    public static int Complexity(VolumePolyhedron polyhedron) => polyhedron.SideCount + Math.Max(0, polyhedron.Top.Count - 1);

    /// <summary>The flat-sided shape over one outline and one reading of the top.</summary>
    private static FlatSidedFit? FitOne(List<(double A, double B)> outline, VolumeTopCandidate candidate, List<(double A, double B, double H)> cells)
    {
        var sides = new FlatSidedBase(outline);
        for (var pass = 0; pass < Passes; pass++)
        {
            var (ring, lines) = sides.Polygon();
            if (ring.Count < 3)
            {
                return null;
            }

            sides.Refit(ring, lines, Inside(candidate, ring), cells);
        }

        var final = VolumeRings.DropShortEdges(sides.Polygon().Ring, MinEdgeMm);
        if (final.Count < 3)
        {
            return null;
        }

        var inside = Inside(candidate, final);
        var faces = VolumeHull.Build(final, inside).Select(f => f.Vertices).ToList();
        var shape = candidate.Shape == "multi-peak" && inside.Count < 2 ? null : candidate.Shape;
        var polyhedron = new VolumePolyhedron(faces, shape);
        var rms = TrimmedRms(polyhedron, cells);
        return new FlatSidedFit(polyhedron, Math.Round(rms, 1), IsGoodFit(rms, polyhedron));
    }

    /// <summary>Within <see cref="GoodRmsMm"/> (or <see cref="GoodRmsShare"/> of the height), or a pyramid or roof within <see cref="LooseRmsMm"/>.</summary>
    /// <param name="rmsMm">The trimmed RMS, mm.</param>
    /// <param name="polyhedron">The fitted shape.</param>
    /// <returns>Whether the fit is good.</returns>
    public static bool IsGoodFit(double rmsMm, VolumePolyhedron polyhedron) =>
        rmsMm <= Math.Max(GoodRmsMm, GoodRmsShare * polyhedron.TopHeightMm)
        || (polyhedron.Shape is "pyramid" or "roof" && rmsMm <= LooseRmsMm);

    /// <summary>A reading's top vertices moved inside the base; a plateau's stay on its plane.</summary>
    private static List<(double A, double B, double H)> Inside(VolumeTopCandidate candidate, IReadOnlyList<(double A, double B)> ring)
    {
        var moved = Inside(candidate.Top, ring);
        return candidate.Plane is not { } p ? moved : moved.Select(t => (t.A, t.B, (p.Alpha * t.A) + (p.Beta * t.B) + p.Gamma)).ToList();
    }

    /// <summary>The top vertices moved inside the base (≥ 20 mm from its edges, less on a small base); two that end up together become one apex.</summary>
    /// <param name="top">The top vertices.</param>
    /// <param name="ring">The base, convex, counter-clockwise.</param>
    /// <returns>The top vertices inside.</returns>
    private static List<(double A, double B, double H)> Inside(IReadOnlyList<(double A, double B, double H)> top, IReadOnlyList<(double A, double B)> ring)
    {
        var centre = (A: ring.Average(p => p.A), B: ring.Average(p => p.B));
        var margin = Math.Min(InsetMm, 0.5 * MinInward(ring, centre));
        var moved = new List<(double A, double B, double H)>();
        foreach (var t in top)
        {
            double lo = 0, hi = 1;
            if (MinInward(ring, (t.A, t.B)) < margin)
            {
                for (var k = 0; k < 30; k++)
                {
                    var mid = (lo + hi) / 2;
                    var p = (t.A + (mid * (centre.A - t.A)), t.B + (mid * (centre.B - t.B)));
                    (lo, hi) = MinInward(ring, p) >= margin ? (lo, mid) : (mid, hi);
                }
            }
            else
            {
                hi = 0;
            }

            moved.Add((t.A + (hi * (centre.A - t.A)), t.B + (hi * (centre.B - t.B)), t.H));
        }

        if (moved.Count == 2 && Math.Sqrt(Sq(moved[0].A - moved[1].A) + Sq(moved[0].B - moved[1].B)) < InsetMm)
        {
            return [((moved[0].A + moved[1].A) / 2, (moved[0].B + moved[1].B) / 2, Math.Max(moved[0].H, moved[1].H))];
        }

        return moved;
    }

    /// <summary>The height field's measured cells inside the detected outline.</summary>
    private static List<(double A, double B, double H)> Cells(VolumeSurface field, IReadOnlyList<(double A, double B)> footprint)
    {
        var cells = new List<(double A, double B, double H)>();
        var hull = PlanePolygon.ConvexHull(footprint);
        if (hull.Count < 3)
        {
            return cells;
        }

        for (var k = 0; k < field.Heights.Count; k++)
        {
            var (a, b) = field.Grid.Centre(k);
            if (field.Heights[k] > 0 && PlanePolygon.Contains(hull, (a, b)))
            {
                cells.Add((a, b, field.Heights[k]));
            }
        }

        return cells;
    }

    /// <summary>The smallest distance from p to the ring's edge lines, positive inside.</summary>
    private static double MinInward(IReadOnlyList<(double A, double B)> ring, (double A, double B) p)
    {
        var min = double.PositiveInfinity;
        for (var i = 0; i < ring.Count; i++)
        {
            var n = VolumeHull.Outward(ring[i], ring[(i + 1) % ring.Count]);
            min = Math.Min(min, -(((p.A - ring[i].A) * n.A) + ((p.B - ring[i].B) * n.B)));
        }

        return min;
    }

    /// <summary>RMS of the height differences without the worst 5 % (holes and holds on the volume).</summary>
    private static double TrimmedRms(VolumePolyhedron polyhedron, List<(double A, double B, double H)> cells)
    {
        var errors = cells.Select(c => Sq(polyhedron.HeightAt(c.A, c.B) - c.H)).Order().ToList();
        var keep = Math.Max(1, (int)Math.Ceiling(errors.Count * 0.95));
        return Math.Sqrt(errors.Take(keep).Average());
    }

    private static double Sq(double x) => x * x;
}
